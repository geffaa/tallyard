using System.Globalization;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Tallyard.Api.Common;
using Tallyard.Api.Infrastructure;
using Tallyard.Api.Modules.Catalog;
using Tallyard.Api.Modules.Inventory;
using Tallyard.Api.Modules.Partners;

namespace Tallyard.Api.Modules.Import;

public record ImportError(int Row, string Column, string Message);
public record ImportResult(string Kind, int Rows, int Valid, int Created, int Updated, bool Committed, ImportError[] Errors, string[] Columns, string?[][] Preview);

/// <summary>
/// Getting a business off spreadsheets: products, customers, suppliers and opening stock from
/// Excel. Upload once to preview (every row validated, errors by row and column); upload again
/// with commit to apply. Committing is all-or-nothing in one transaction, and only when the file
/// has no errors, so a half-imported catalog never happens. Existing rows (same SKU or code) are
/// updated, so a corrected file can be imported again.
/// </summary>
public static class ImportEndpoints
{
    record Column(string Name, bool Required, string Example);

    static readonly Dictionary<string, (string Perm, Column[] Columns)> Kinds = new()
    {
        ["products"] = (Perm.EditCatalog,
        [
            new("sku", true, "BEV-NEW-0001"), new("name", true, "Northvale Lemonade 330 ml"), new("category", false, "Beverages"),
            new("base_unit", true, "can"), new("case_size", false, "24"), new("sale_price", false, "1.20"), new("tax_rate", false, "0"),
            new("track_batches", false, "no"), new("unit_barcode", false, "0123456789012"), new("case_barcode", false, "1012345678901"),
        ]),
        ["customers"] = (Perm.EditCustomers,
        [
            new("code", true, "CUS-900"), new("name", true, "Lakeside Grocers"), new("email", false, "ap@lakeside.test"), new("phone", false, ""),
            new("address", false, ""), new("tax_id", false, ""), new("payment_terms_days", false, "30"), new("credit_limit", false, "10000"),
        ]),
        ["suppliers"] = (Perm.EditSuppliers,
        [
            new("code", true, "SUP-900"), new("name", true, "Northwind Foods"), new("email", false, "orders@northwind.test"), new("phone", false, ""),
            new("address", false, ""), new("tax_id", false, ""), new("payment_terms_days", false, "30"), new("lead_time_days", false, "7"),
        ]),
        ["opening_stock"] = (Perm.AdjustStock,
        [
            new("warehouse", true, "CHI"), new("sku", true, "BEV-NEW-0001"), new("quantity", true, "48"), new("unit", false, "can"),
            new("unit_cost", false, "0.70"), new("batch", false, ""), new("expiry", false, "2027-03-31"),
        ]),
    };

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/import");

        g.MapGet("/{kind}/template", (string kind, CurrentUser me) =>
        {
            var spec = Spec(kind, me);
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet(kind);
            for (var i = 0; i < spec.Length; i++)
            {
                ws.Cell(1, i + 1).Value = spec[i].Name;
                ws.Cell(1, i + 1).Style.Font.Bold = spec[i].Required;
                ws.Cell(2, i + 1).Value = spec[i].Example;
            }
            ws.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#E3EFED");
            ws.Columns().AdjustToContents();
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return Results.File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"tallyard-{kind}.xlsx");
        });

        g.MapPost("/{kind}", async (string kind, bool? commit, IFormFile file, AppDbContext db, CurrentUser me, WarehouseAccess access,
            StockLedger ledger, HttpContext http, CancellationToken ct) =>
        {
            var spec = Spec(kind, me);
            var lang = Messages.LangOf(http);
            List<Dictionary<string, string?>> rows;
            try
            {
                rows = Read(file, spec);
            }
            catch (Exception e) when (e is not AppException)
            {
                throw new AppException(422, "import_not_excel");
            }
            if (rows.Count > 5000) throw new AppException(422, "import_too_many", ("max", 5000));

            var errors = new List<ImportError>();
            void Error(int row, string column, string code, params (string, object)[] vars) =>
                errors.Add(new ImportError(row + 2, column, Messages.Render(code, lang, vars.ToDictionary(v => v.Item1, v => v.Item2))));
            foreach (var (row, i) in rows.Select((r, i) => (r, i)))
                foreach (var c in spec.Where(c => c.Required && string.IsNullOrWhiteSpace(row[c.Name])))
                    Error(i, c.Name, "import_required");

            var importer = new Importer(db, me, access, ledger, rows, Error);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var (created, updated) = kind switch
            {
                "products" => await importer.Products(ct),
                "customers" => await importer.Partners<Customer>(ct),
                "suppliers" => await importer.Partners<Supplier>(ct),
                _ => await importer.OpeningStock(ct),
            };
            var ok = commit == true && errors.Count == 0;
            if (ok)
            {
                await tx.CommitAsync(ct);
                await ledger.PublishAsync(ct);
            }
            else await tx.RollbackAsync(ct); // preview: everything was checked against the database, nothing kept

            var badRows = errors.Select(e => e.Row).Distinct().Count();
            return new ImportResult(kind, rows.Count, rows.Count - badRows, created, updated, ok,
                errors.OrderBy(e => e.Row).Take(200).ToArray(), spec.Select(c => c.Name).ToArray(),
                rows.Take(20).Select(r => spec.Select(c => r[c.Name]).ToArray()).ToArray());
        }).DisableAntiforgery();
    }

    static Column[] Spec(string kind, CurrentUser me)
    {
        if (!Kinds.TryGetValue(kind, out var spec)) throw AppException.NotFound("import_unknown_kind");
        if (!Perm.Has(me.Role!.Value, spec.Perm)) throw AppException.Forbidden();
        return spec.Columns;
    }

    /// <summary>Rows of the first sheet by header name (case and spaces ignored); blank rows skipped.</summary>
    static List<Dictionary<string, string?>> Read(IFormFile file, Column[] spec)
    {
        using var stream = file.OpenReadStream();
        using var wb = new XLWorkbook(stream);
        var ws = wb.Worksheets.First();
        var header = ws.Row(1).CellsUsed().ToDictionary(c => c.GetString().Trim().ToLowerInvariant().Replace(' ', '_'), c => c.Address.ColumnNumber);
        var missing = spec.Where(c => c.Required && !header.ContainsKey(c.Name)).Select(c => c.Name).ToList();
        if (missing.Count > 0) throw new AppException(422, "import_missing_columns", ("columns", string.Join(", ", missing)));
        var rows = new List<Dictionary<string, string?>>();
        var last = ws.LastRowUsed()?.RowNumber() ?? 1;
        for (var r = 2; r <= last; r++)
        {
            var row = spec.ToDictionary(c => c.Name, c => header.TryGetValue(c.Name, out var col) ? Cell(ws.Cell(r, col)) : null);
            if (row.Values.All(string.IsNullOrWhiteSpace)) continue;
            rows.Add(row);
        }
        return rows;
    }

    // Numbers and dates as typed in Excel come back as numbers or dates, not text.
    static string? Cell(IXLCell c) => c.DataType switch
    {
        XLDataType.Blank => null,
        XLDataType.Number => c.GetDouble().ToString(CultureInfo.InvariantCulture),
        XLDataType.DateTime => c.GetDateTime().ToString("yyyy-MM-dd"),
        XLDataType.Boolean => c.GetBoolean() ? "yes" : "no",
        _ => c.GetString().Trim() is { Length: > 0 } s ? s : null,
    };

    sealed class Importer(AppDbContext db, CurrentUser me, WarehouseAccess access, StockLedger ledger,
        List<Dictionary<string, string?>> rows, Action<int, string, string, (string, object)[]> error)
    {
        void Error(int row, string col, string code, params (string, object)[] vars) => error(row, col, code, vars);

        decimal? Number(int i, string col, decimal? min = 0)
        {
            var v = rows[i][col];
            if (v is null) return null;
            if (!decimal.TryParse(v.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) || d < min)
            {
                Error(i, col, "import_number");
                return null;
            }
            return d;
        }

        bool Yes(string? v) => v?.ToLowerInvariant() is "yes" or "y" or "true" or "1" or "ya";

        void Duplicates(string col)
        {
            foreach (var g in rows.Select((r, i) => (Key: r[col]?.ToUpperInvariant(), i)).Where(x => x.Key != null).GroupBy(x => x.Key).Where(g => g.Count() > 1))
                foreach (var (_, i) in g.Skip(1)) Error(i, col, "import_duplicate", ("value", g.Key!));
        }

        public async Task<(int, int)> Products(CancellationToken ct)
        {
            Duplicates("sku");
            var categories = await db.Categories.ToDictionaryAsync(c => c.Name.ToLowerInvariant(), ct);
            var existing = await db.Products.Include(p => p.Units).Include(p => p.Barcodes).ToDictionaryAsync(p => p.Sku, ct);
            var barcodeOwners = await db.ProductBarcodes.AsNoTracking().Join(db.Products, b => b.ProductId, p => p.Id, (b, p) => new { b.Code, p.Sku })
                .ToDictionaryAsync(x => x.Code, x => x.Sku, ct);
            int created = 0, updated = 0;
            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (r["sku"] is not { } rawSku || r["name"] is not { } name || r["base_unit"] is not { } baseUnit) continue;
                var sku = rawSku.ToUpperInvariant();
                if (!System.Text.RegularExpressions.Regex.IsMatch(sku, "^[A-Z0-9._-]+$")) { Error(i, "sku", "import_invalid"); continue; }
                var caseSize = Number(i, "case_size", 2);
                var price = Number(i, "sale_price");
                var tax = Number(i, "tax_rate");
                if (tax > 100) Error(i, "tax_rate", "import_number");
                foreach (var col in new[] { "unit_barcode", "case_barcode" })
                    if (r[col] is { } code && barcodeOwners.TryGetValue(code, out var owner) && owner != sku)
                        Error(i, col, "barcode_taken", ("barcode", code), ("sku", owner));

                Category? category = null;
                if (r["category"] is { } catName && !categories.TryGetValue(catName.ToLowerInvariant(), out category))
                {
                    category = new Category { Name = catName };
                    categories[catName.ToLowerInvariant()] = category;
                    db.Categories.Add(category);
                }

                if (!existing.TryGetValue(sku, out var p))
                {
                    p = new Product { Sku = sku, Name = name };
                    db.Products.Add(p);
                    existing[sku] = p;
                    created++;
                }
                else updated++;
                p.Name = name;
                p.BaseUnit = baseUnit.ToLowerInvariant();
                p.CategoryId = category?.Id ?? p.CategoryId;
                p.SalePrice = price ?? p.SalePrice;
                p.TaxRate = tax ?? p.TaxRate;
                if (r["track_batches"] is { } tb) p.TrackBatches = Yes(tb);
                if (caseSize is { } size)
                {
                    var unit = p.Units.FirstOrDefault(u => u.Name == "case");
                    if (unit is null) p.Units.Add(new ProductUnit { ProductId = p.Id, Name = "case", Factor = size });
                    else unit.Factor = size;
                }
                foreach (var (col, unitName) in new[] { ("unit_barcode", p.BaseUnit), ("case_barcode", "case") })
                    if (r[col] is { } code && p.Barcodes.All(b => b.Code != code))
                    {
                        p.Barcodes.Add(new ProductBarcode { ProductId = p.Id, Code = code, Unit = unitName });
                        barcodeOwners[code] = sku;
                    }
            }
            await db.SaveChangesAsync(ct);
            return (created, updated);
        }

        public async Task<(int, int)> Partners<T>(CancellationToken ct) where T : Partner, new()
        {
            Duplicates("code");
            var existing = await db.Set<T>().ToDictionaryAsync(p => p.Code, ct);
            int created = 0, updated = 0;
            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (r["code"] is not { } rawCode || r["name"] is not { } name) continue;
                var code = rawCode.ToUpperInvariant();
                if (r["email"] is { } email && !email.Contains('@')) Error(i, "email", "import_invalid");
                var terms = Number(i, "payment_terms_days");
                if (!existing.TryGetValue(code, out var e))
                {
                    e = new T { Code = code, Name = name };
                    db.Set<T>().Add(e);
                    existing[code] = e;
                    created++;
                }
                else updated++;
                e.Name = name;
                e.Email = r["email"] ?? e.Email;
                e.Phone = r["phone"] ?? e.Phone;
                e.Address = r["address"] ?? e.Address;
                e.TaxId = r["tax_id"] ?? e.TaxId;
                e.PaymentTermsDays = (int)(terms ?? e.PaymentTermsDays);
                if (e is Customer c && Number(i, "credit_limit") is { } limit) c.CreditLimit = limit;
                if (e is Supplier s && Number(i, "lead_time_days") is { } lead) s.LeadTimeDays = (int)lead;
            }
            await db.SaveChangesAsync(ct);
            return (created, updated);
        }

        /// <summary>Opening balances go through the stock ledger like any adjustment (reason "opening").</summary>
        public async Task<(int, int)> OpeningStock(CancellationToken ct)
        {
            var allowed = await access.AllowedAsync(ct);
            var warehouses = await db.Warehouses.AsNoTracking().Where(w => w.IsActive && (allowed == null || allowed.Contains(w.Id)))
                .ToDictionaryAsync(w => w.Code, ct);
            var products = await db.Products.AsNoTracking().Include(p => p.Units).ToDictionaryAsync(p => p.Sku, ct);
            var changes = new List<(Guid Warehouse, StockChange Change)>();
            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (r["warehouse"] is not { } wcode || r["sku"] is not { } sku) continue;
                if (!warehouses.TryGetValue(wcode.ToUpperInvariant(), out var w)) { Error(i, "warehouse", "import_unknown_warehouse", ("code", wcode)); continue; }
                if (!products.TryGetValue(sku.ToUpperInvariant(), out var p)) { Error(i, "sku", "import_unknown_sku", ("sku", sku)); continue; }
                var qty = Number(i, "quantity", 0.0001m);
                var cost = Number(i, "unit_cost");
                var factor = 1m;
                if (r["unit"] is { } unit && !string.Equals(unit, p.BaseUnit, StringComparison.OrdinalIgnoreCase))
                {
                    var u = p.Units.FirstOrDefault(x => string.Equals(x.Name, unit, StringComparison.OrdinalIgnoreCase));
                    if (u is null) { Error(i, "unit", "unknown_unit", ("unit", unit), ("sku", p.Sku)); continue; }
                    factor = u.Factor;
                }
                DateOnly? expiry = null;
                if (r["expiry"] is { } exp)
                {
                    if (DateOnly.TryParse(exp, CultureInfo.InvariantCulture, out var d)) expiry = d;
                    else Error(i, "expiry", "import_bad_date");
                }
                if (p.TrackBatches && r["batch"] is null) { Error(i, "batch", "batch_required", ("sku", p.Sku)); continue; }
                if (qty is null) continue;
                Guid? batchId = p.TrackBatches ? (await ledger.BatchAsync(p.Id, r["batch"]!, expiry, ct)).Id : null;
                changes.Add((w.Id, new StockChange(p.Id, w.Id, qty.Value * factor, batchId, cost is { } c ? c / factor : null)));
            }
            if (changes.Count > 0)
                foreach (var g in changes.GroupBy(c => c.Warehouse))
                    await ledger.PostAsync(new PostRequest(OperationKind.Adjustment, "ADJ", g.Select(x => x.Change).ToList(),
                        Reason: "opening", Note: "Imported from Excel"), ct);
            return (changes.Count, 0);
        }
    }
}
