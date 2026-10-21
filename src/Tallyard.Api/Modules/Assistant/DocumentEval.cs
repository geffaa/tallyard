using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Tallyard.Api.Infrastructure;
using Tallyard.Api.Modules.Catalog;
using Tallyard.Api.Modules.Identity;

namespace Tallyard.Api.Modules.Assistant;

public record TruthLine(string Sku, decimal Quantity, string Unit, decimal UnitPrice);
public record TruthDoc(string File, string Style, string Supplier, string Number, string Date, decimal Total, TruthLine[] Lines);

/// <summary>
/// An evaluation set for supplier-document reading, and its runner.
///
/// <c>dotnet run -- eval-docs generate</c> writes 30 fictional supplier documents (PDF and PNG) in
/// four styles that make matching progressively harder, with the right answers in truth.json:
/// <list type="bullet">
/// <item><b>clean</b>: full product names, "case" units;</item>
/// <item><b>abbreviated</b>: "BLCRST SPRK WTR 1L", "CS", the supplier's own item codes;</item>
/// <item><b>barcode</b>: our case barcodes in the code column;</item>
/// <item><b>european</b>: "1.234,50" numbers and "ctn" units.</item>
/// </list>
/// Freight and discount rows are added to some documents and must be skipped.
/// <c>dotnet run -- eval-docs run</c> reads every document through the real pipeline (Groq) and
/// writes results.md: what was read correctly, and how often a confident match was wrong.
/// </summary>
public static class DocumentEval
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    static readonly Dictionary<string, string> Abbrev = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sparkling"] = "SPRK", ["Water"] = "WTR", ["Chocolate"] = "CHOC", ["Coffee"] = "COF", ["Orange"] = "ORG", ["Juice"] = "JCE",
        ["Green"] = "GRN", ["Ginger"] = "GNGR", ["Sea"] = "SEA", ["Salt"] = "SLT", ["Chips"] = "CHP", ["Dark"] = "DK", ["Bar"] = "BR",
        ["Trail"] = "TRL", ["Rice"] = "RCE", ["Crackers"] = "CRKR", ["Cookies"] = "CKIE", ["Greek"] = "GRK", ["Yogurt"] = "YOG",
        ["Cheddar"] = "CHDR", ["Block"] = "BLK", ["Whole"] = "WHL", ["Milk"] = "MLK", ["Salted"] = "SLTD", ["Butter"] = "BTR",
        ["Basmati"] = "BSMT", ["Penne"] = "PNE", ["Pasta"] = "PST", ["Olive"] = "OLV", ["Oil"] = "OIL", ["Tomato"] = "TOM",
        ["Passata"] = "PSST", ["Rolled"] = "RLD", ["Oats"] = "OATS", ["Dish"] = "DSH", ["Soap"] = "SP", ["Laundry"] = "LNDRY",
        ["Pods"] = "PDS", ["Paper"] = "PPR", ["Towels"] = "TWL", ["Trash"] = "TRSH", ["Bags"] = "BGS", ["Hand"] = "HND",
        ["Toothpaste"] = "TPASTE", ["Shampoo"] = "SHMP", ["Body"] = "BDY", ["Lotion"] = "LTN", ["Cold"] = "CLD", ["Brew"] = "BRW", ["Tea"] = "TEA",
    };

    static string Shorten(string name)
    {
        var words = name.Replace(" ml", "ML").Replace(" g", "G").Replace(" L", "L").Replace(" kg", "KG").Replace(" ct", "CT").Split(' ');
        return string.Join(' ', words.Select((w, i) => i == 0 ? new string(w.Where(c => !"aeiou".Contains(c)).ToArray()).ToUpperInvariant()
            : Abbrev.TryGetValue(w, out var a) ? a : w.ToUpperInvariant()));
    }

    public static async Task GenerateAsync(IConfiguration config, string dir)
    {
        Directory.CreateDirectory(dir);
        var current = new CurrentUser();
        await using (var owner = Db.Owner(config, current))
            current.Set((await owner.Tenants.FirstAsync(t => t.Slug == "harborpine")).Id);
        await using var db = DbApp.Create(config, current);
        var rng = new Random(2026);
        var suppliers = await db.Suppliers.AsNoTracking().ToListAsync();
        var products = await db.Products.AsNoTracking().Include(p => p.Units).Include(p => p.Barcodes).Include(p => p.Category).ToListAsync();
        var costs = await db.StockBalances.AsNoTracking().GroupBy(b => b.ProductId).Select(g => new { g.Key, C = g.Max(b => b.AvgCost) })
            .ToDictionaryAsync(x => x.Key, x => x.C);
        var byCategory = new Dictionary<string, string>
        {
            ["Beverages"] = "Beverage", ["Snacks"] = "Snack", ["Dairy & Chilled"] = "Dairy", ["Pantry"] = "Pantry",
            ["Household"] = "Household", ["Personal Care"] = "Personal",
        };
        string[] styles = ["clean", "abbreviated", "barcode", "european"];
        var truths = new List<TruthDoc>();

        for (var i = 0; i < 30; i++)
        {
            var style = styles[i % styles.Length];
            var category = byCategory.Keys.ElementAt(rng.Next(byCategory.Count));
            var supplier = suppliers.First(s => s.Name.Contains(byCategory[category]));
            var pool = products.Where(p => p.Category?.Name == category && p.Units.Count > 0 && costs.ContainsKey(p.Id)).OrderBy(_ => rng.Next()).Take(rng.Next(3, 9)).ToList();
            var date = new DateOnly(2026, 9, 1).AddDays(rng.Next(0, 45));
            var number = style switch { "european" => $"RE-{rng.Next(10000, 99999)}", "barcode" => $"{rng.Next(100000, 999999)}", _ => $"INV-{rng.Next(1000, 9999)}" };
            var lines = pool.Select(p =>
            {
                var pack = p.Units.OrderByDescending(u => u.Factor).First();
                var inCases = style != "clean" || rng.NextDouble() < 0.8;
                var qty = inCases ? rng.Next(1, 30) : rng.Next(6, 60);
                var price = Math.Round(costs[p.Id] * (inCases ? pack.Factor : 1), 2);
                return (Product: p, Pack: pack, InCases: inCases, Qty: (decimal)qty, Price: price);
            }).ToList();
            var subtotal = lines.Sum(l => l.Qty * l.Price);
            var freight = rng.NextDouble() < 0.35 ? Math.Round((decimal)rng.Next(15, 60), 2) : 0;
            var tax = Math.Round((subtotal + freight) * 0.07m, 2);
            var total = subtotal + freight + tax;

            var rows = lines.Select(l =>
            {
                var unitText = l.InCases ? style switch { "abbreviated" => "CS", "european" => "ctn", _ => l.Pack.Name } : style == "abbreviated" ? "EA" : l.Product.BaseUnit;
                var code = style switch
                {
                    "barcode" => l.InCases ? l.Product.Barcodes.FirstOrDefault(b => b.Unit == l.Pack.Name)?.Code ?? "" : l.Product.Barcodes.FirstOrDefault(b => b.Unit == l.Product.BaseUnit)?.Code ?? "",
                    "abbreviated" => $"{supplier.Code[^3..]}-{l.Product.Sku.Sum(c => c) * 7 % 9000 + 1000}",
                    _ => "",
                };
                var desc = style == "abbreviated" ? Shorten(l.Product.Name) : l.Product.Name;
                return new Row(code, desc, l.Qty, unitText, l.Price, l.Qty * l.Price);
            }).ToList();
            if (freight > 0) rows.Add(new Row("", style == "european" ? "Frachtkosten / Freight" : "Freight & handling", 1, "", freight, freight));

            var image = i % 3 == 2; // every third document arrives as a photo-like PNG
            var file = $"doc-{i + 1:D2}-{style}.{(image ? "png" : "pdf")}";
            var docBytes = Render(supplier.Name, supplier.Address, number, date, rows, subtotal + freight, tax, total, style == "european");
            await File.WriteAllBytesAsync(Path.Combine(dir, file), docBytes(image));

            truths.Add(new TruthDoc(file, style, supplier.Name, number, date.ToString("yyyy-MM-dd"), total,
                lines.Select(l => new TruthLine(l.Product.Sku, l.Qty, l.InCases ? l.Pack.Name : l.Product.BaseUnit, l.Price)).ToArray()));
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "truth.json"), JsonSerializer.Serialize(truths, Json));
        Console.WriteLine($"Wrote {truths.Count} documents to {dir}");
    }

    record Row(string Code, string Description, decimal Qty, string Unit, decimal Price, decimal Amount);

    static Func<bool, byte[]> Render(string supplier, string? address, string number, DateOnly date, List<Row> rows,
        decimal net, decimal tax, decimal total, bool european)
    {
        var culture = european ? CultureInfo.GetCultureInfo("de-DE") : CultureInfo.InvariantCulture;
        string N(decimal v) => v.ToString("#,##0.00", culture);
        string Q(decimal v) => v.ToString("0.##", culture);
        var doc = Document.Create(d => d.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(36);
            page.DefaultTextStyle(t => t.FontSize(10));
            page.Header().Row(r =>
            {
                r.RelativeItem().Column(c =>
                {
                    c.Item().Text(supplier).FontSize(15).Bold();
                    if (address is not null) c.Item().Text(address).FontColor(Colors.Grey.Darken1);
                });
                r.ConstantItem(200).AlignRight().Column(c =>
                {
                    c.Item().AlignRight().Text(european ? "RECHNUNG / INVOICE" : "INVOICE").Bold();
                    c.Item().AlignRight().Text($"No. {number}");
                    c.Item().AlignRight().Text(european ? date.ToString("dd.MM.yyyy") : date.ToString("MMM d, yyyy", CultureInfo.InvariantCulture));
                });
            });
            page.Content().PaddingTop(20).Column(col =>
            {
                col.Item().Text("Bill to: Harbor & Pine Supply Co., Chicago DC").FontColor(Colors.Grey.Darken2);
                col.Item().PaddingTop(12).Table(t =>
                {
                    t.ColumnsDefinition(c => { c.ConstantColumn(95); c.RelativeColumn(); c.ConstantColumn(45); c.ConstantColumn(40); c.ConstantColumn(65); c.ConstantColumn(75); });
                    foreach (var h in new[] { "Item code", "Description", "Qty", "Unit", "Price", "Amount" })
                        t.Cell().BorderBottom(1).PaddingVertical(4).PaddingLeft(h == "Unit" ? 8 : 0).Text(h).Bold();
                    foreach (var r in rows)
                    {
                        t.Cell().PaddingVertical(3).Text(r.Code).FontSize(8.5f);
                        t.Cell().PaddingVertical(3).Text(r.Description);
                        t.Cell().PaddingVertical(3).AlignRight().Text(Q(r.Qty));
                        t.Cell().PaddingVertical(3).PaddingLeft(8).Text(r.Unit);
                        t.Cell().PaddingVertical(3).AlignRight().Text(N(r.Price));
                        t.Cell().PaddingVertical(3).AlignRight().Text(N(r.Amount));
                    }
                });
                col.Item().PaddingTop(10).AlignRight().Width(200).Column(c =>
                {
                    c.Item().Row(r => { r.RelativeItem().Text("Net"); r.AutoItem().Text(N(net)); });
                    c.Item().Row(r => { r.RelativeItem().Text("Sales tax 7%"); r.AutoItem().Text(N(tax)); });
                    c.Item().Row(r => { r.RelativeItem().Text("Total due").Bold(); r.AutoItem().Text(N(total) + " USD").Bold(); });
                });
            });
        }));
        return image => image ? doc.GenerateImages(new ImageGenerationSettings { RasterDpi = 110 }).First() : doc.GeneratePdf();
    }

    public static async Task RunAsync(IConfiguration config, IServiceProvider services, string dir)
    {
        var truths = JsonSerializer.Deserialize<List<TruthDoc>>(await File.ReadAllTextAsync(Path.Combine(dir, "truth.json")), Json)!;
        var current = new CurrentUser();
        await using (var owner = Db.Owner(config, current))
        {
            var t = await owner.Tenants.FirstAsync(x => x.Slug == "harborpine");
            var user = await owner.Users.FirstAsync(u => u.TenantId == t.Id && u.Role == Role.Purchasing);
            current.Set(t.Id, user.Id, Role.Purchasing);
        }
        await using var db = DbApp.Create(config, current);
        var extraction = new Extraction(db, services.GetRequiredService<ILlm>(), services.GetRequiredService<AiOptions>());
        var report = new StringBuilder();
        var stats = new Dictionary<string, int>();
        void Count(string key, bool ok = true) { if (ok) stats[key] = stats.GetValueOrDefault(key) + 1; }

        report.AppendLine("| Document | Style | Read via | Supplier | Number | Date | Total | Lines found | Correct product | Correct qty & price | Wrong but unflagged | Seconds |");
        report.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var t in truths)
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(dir, t.File));
            var type = t.File.EndsWith(".pdf") ? "application/pdf" : "image/png";
            Count("docs");
            try
            {
                var (r, reader, _, ms) = await extraction.RunAsync(bytes, type, default);
                var e = r.Extracted;
                bool supplierOk = r.Supplier?.Name == t.Supplier, numberOk = e.DocumentNumber?.Replace("No. ", "") == t.Number,
                     dateOk = e.DocumentDate == t.Date, totalOk = e.Total is { } tot && Math.Abs(tot - t.Total) < 0.02m;
                Count("supplier", supplierOk); Count("number", numberOk); Count("date", dateOk); Count("total", totalOk);
                Count("lines_expected", false);
                stats["lines_expected"] = stats.GetValueOrDefault("lines_expected") + t.Lines.Length;

                int product = 0, values = 0, silentWrong = 0;
                foreach (var truth in t.Lines)
                {
                    var hit = r.Lines.FirstOrDefault(l => l.Sku == truth.Sku);
                    if (hit is null) continue;
                    product++;
                    if (hit.Quantity == truth.Quantity && hit.UnitPrice == truth.UnitPrice && hit.Unit == truth.Unit) values++;
                }
                // A line matched to a product that isn't on the document, with no flag: the dangerous case.
                silentWrong = r.Lines.Count(l => l.ProductId is not null && l.Flags.Length == 0 && t.Lines.All(x => x.Sku != l.Sku));
                stats["lines_found"] = stats.GetValueOrDefault("lines_found") + r.Lines.Length;
                stats["product_ok"] = stats.GetValueOrDefault("product_ok") + product;
                stats["values_ok"] = stats.GetValueOrDefault("values_ok") + values;
                stats["auto"] = stats.GetValueOrDefault("auto") + r.Lines.Count(l => l.ProductId is not null && l.Flags.Length == 0);
                stats["silent_wrong"] = stats.GetValueOrDefault("silent_wrong") + silentWrong;
                stats[$"style:{t.Style}:expected"] = stats.GetValueOrDefault($"style:{t.Style}:expected") + t.Lines.Length;
                stats[$"style:{t.Style}:ok"] = stats.GetValueOrDefault($"style:{t.Style}:ok") + product;
                stats["ms"] = stats.GetValueOrDefault("ms") + ms;
                string Y(bool b) => b ? "✓" : "✗";
                report.AppendLine($"| {t.File} | {t.Style} | {reader} | {Y(supplierOk)} | {Y(numberOk)} | {Y(dateOk)} | {Y(totalOk)} | {r.Lines.Length}/{t.Lines.Length} | {product}/{t.Lines.Length} | {values}/{t.Lines.Length} | {silentWrong} | {(ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)} |");
            }
            catch (Exception ex)
            {
                Count("failed");
                report.AppendLine($"| {t.File} | {t.Style} | failed: {ex.Message} | | | | | | | | | |");
            }
            Console.WriteLine($"{t.File} done");
        }

        int S(string k) => stats.GetValueOrDefault(k);
        string P(int a, int b) => b == 0 ? "–" : $"{100.0 * a / b:0}%";
        var summary = new StringBuilder();
        summary.AppendLine($"# Supplier document reading: evaluation ({DateTime.UtcNow:yyyy-MM-dd})");
        summary.AppendLine();
        summary.AppendLine($"{S("docs")} documents ({truths.Count(t => t.File.EndsWith(".pdf"))} PDF, {truths.Count(t => t.File.EndsWith(".png"))} PNG), {S("lines_expected")} product lines.");
        summary.AppendLine();
        summary.AppendLine("| Measure | Result |");
        summary.AppendLine("|---|---|");
        summary.AppendLine($"| Documents read | {P(S("docs") - S("failed"), S("docs"))} |");
        summary.AppendLine($"| Supplier matched | {P(S("supplier"), S("docs"))} |");
        summary.AppendLine($"| Document number / date / total | {P(S("number"), S("docs"))} / {P(S("date"), S("docs"))} / {P(S("total"), S("docs"))} |");
        summary.AppendLine($"| Lines matched to the right product | {P(S("product_ok"), S("lines_expected"))} |");
        summary.AppendLine($"| … with quantity, unit and price right | {P(S("values_ok"), S("lines_expected"))} |");
        summary.AppendLine($"| Lines accepted without a flag | {P(S("auto"), S("lines_found"))} |");
        summary.AppendLine($"| Wrong product with no flag (silent errors) | {S("silent_wrong")} of {S("auto")} |");
        foreach (var style in new[] { "clean", "abbreviated", "barcode", "european" })
            summary.AppendLine($"| Right product, {style} style | {P(S($"style:{style}:ok"), S($"style:{style}:expected"))} |");
        summary.AppendLine($"| Average time per document | {(S("ms") / Math.Max(1, S("docs") - S("failed")) / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)} s |");
        summary.AppendLine();
        summary.Append(report);
        await File.WriteAllTextAsync(Path.Combine(dir, "results.md"), summary.ToString());
        Console.WriteLine(summary.ToString().Split("\n| Document")[0]);
    }
}
