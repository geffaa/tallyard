using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Tallyard.Api.Infrastructure;
using Tallyard.Api.Modules.Purchasing;
using Tallyard.Api.Modules.Sales;

namespace Tallyard.Api.Modules.Assistant;

/// <summary>A result shown to the person as a table beside the answer, so every number can be checked.</summary>
public record DataTable(string Title, string[] Columns, object?[][] Rows);

/// <summary>
/// The only things the data chat can do. Each tool is a fixed query that runs as the person
/// asking: row-level security limits it to their tenant, warehouse access to their warehouses,
/// and cost or price columns are left out for roles that may not see them. The model never writes
/// SQL, so it can't reach data the screen wouldn't show.
/// </summary>
public sealed class DataTools(AppDbContext db, CurrentUser me, WarehouseAccess access, Reorder reorder)
{
    public static readonly JsonArray Definitions = (JsonArray)JsonNode.Parse("""
    [
      { "type": "function", "function": { "name": "find_products",
        "description": "Search products by name, SKU or barcode. Returns SKU, name, unit and total stock.",
        "parameters": { "type": "object", "properties": { "query": { "type": "string" } }, "required": ["query"] } } },
      { "type": "function", "function": { "name": "stock_by_warehouse",
        "description": "Stock of products matching a query, per warehouse: on hand, reserved for orders, available, and on order from suppliers.",
        "parameters": { "type": "object", "properties": { "query": { "type": "string" }, "warehouse": { "type": "string", "description": "Warehouse code or city, optional" } }, "required": ["query"] } } },
      { "type": "function", "function": { "name": "reorder_needed",
        "description": "Products at or below their reorder point (counting stock already on order), with the suggested order.",
        "parameters": { "type": "object", "properties": { "warehouse": { "type": "string" } } } } },
      { "type": "function", "function": { "name": "expiring_batches",
        "description": "Batches in stock that expire within the given number of days (default 30).",
        "parameters": { "type": "object", "properties": { "days": { "type": "integer" }, "warehouse": { "type": "string" } } } } },
      { "type": "function", "function": { "name": "sales_summary",
        "description": "Shipped sales between two dates, grouped by product, customer, warehouse or month. Quantities in base units; revenue excludes tax.",
        "parameters": { "type": "object", "properties": {
          "from": { "type": "string", "description": "YYYY-MM-DD" }, "to": { "type": "string", "description": "YYYY-MM-DD" },
          "group_by": { "type": "string", "enum": ["product", "customer", "warehouse", "month"] },
          "limit": { "type": "integer" } }, "required": ["from", "to", "group_by"] } } },
      { "type": "function", "function": { "name": "product_velocity",
        "description": "How fast products matching a query sell (average per day over the last 30 and 90 days) and how many days the available stock lasts.",
        "parameters": { "type": "object", "properties": { "query": { "type": "string" }, "warehouse": { "type": "string" } }, "required": ["query"] } } },
      { "type": "function", "function": { "name": "receivables",
        "description": "What customers owe, by how long it is overdue. Optionally for one customer.",
        "parameters": { "type": "object", "properties": { "customer": { "type": "string" } } } } },
      { "type": "function", "function": { "name": "open_purchase_orders",
        "description": "Purchase orders not yet fully received, with supplier, expected date and progress.",
        "parameters": { "type": "object", "properties": { "supplier": { "type": "string" }, "overdue_only": { "type": "boolean" } } } } }
    ]
    """)!;

    public async Task<(string Json, DataTable? Table)> RunAsync(string name, string arguments, CancellationToken ct)
    {
        var args = JsonNode.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments)?.AsObject() ?? [];
        string? S(string key) => args[key]?.GetValue<string>() is { Length: > 0 } v ? v : null;
        var table = name switch
        {
            "find_products" => await FindProducts(S("query") ?? "", ct),
            "stock_by_warehouse" => await StockByWarehouse(S("query") ?? "", S("warehouse"), ct),
            "reorder_needed" => await ReorderNeeded(S("warehouse"), ct),
            "expiring_batches" => await Expiring(args["days"]?.GetValue<int>() ?? 30, S("warehouse"), ct),
            "sales_summary" => await Sales(S("from"), S("to"), S("group_by") ?? "product", args["limit"]?.GetValue<int>() ?? 10, ct),
            "product_velocity" => await Velocity(S("query") ?? "", S("warehouse"), ct),
            "receivables" => await Receivables(S("customer"), ct),
            "open_purchase_orders" => await OpenPurchaseOrders(S("supplier"), args["overdue_only"]?.GetValue<bool>() ?? false, ct),
            _ => null,
        };
        if (table is null) return ("""{"error":"unknown tool"}""", null);
        // The model gets the first rows only, columns named once: the free tier allows 8,000 tokens
        // a minute, and the person sees the full table anyway. totalRows tells the model what it isn't seeing.
        const int ForModel = 15;
        // The count goes first and in words: given only "totalRows", the model still sometimes
        // counted the visible rows instead (said 13 when there were 51).
        var count = table.Rows.Length > ForModel
            ? $"{table.Rows.Length} rows in total; only the first {ForModel} are listed below. The answer must say {table.Rows.Length}."
            : $"{table.Rows.Length} rows in total, all listed below.";
        return (JsonSerializer.Serialize(new { count, table.Title, table.Columns, rows = table.Rows.Take(ForModel) }), table);
    }

    bool Can(string perm) => Perm.Has(me.Role!.Value, perm);

    static DataTable Denied(string title) => new(title, ["note"], [["Your role can't see this information."]]);

    async Task<List<Guid>> Warehouses(string? hint, CancellationToken ct)
    {
        var allowed = await access.AllowedAsync(ct);
        var q = db.Warehouses.AsNoTracking().Where(w => w.IsActive && (allowed == null || allowed.Contains(w.Id)));
        if (hint is { } h)
            q = q.Where(w => w.Code == h.ToUpper() || EF.Functions.ILike(w.Name, $"%{h}%") || EF.Functions.ILike(w.City ?? "", $"%{h}%"));
        return await q.Select(w => w.Id).ToListAsync(ct);
    }

    IQueryable<Catalog.Product> Match(string query)
    {
        var term = query.Trim();
        return db.Products.AsNoTracking().Where(p => p.IsActive && (p.Sku == term.ToUpper() || p.Barcodes.Any(b => b.Code == term)
            || EF.Functions.ILike(p.Name, $"%{term}%") || EF.Functions.TrigramsWordSimilarity(term, p.Name) > 0.5))
            .OrderByDescending(p => EF.Functions.TrigramsWordSimilarity(term, p.Name));
    }

    async Task<DataTable> FindProducts(string query, CancellationToken ct)
    {
        var whs = await Warehouses(null, ct);
        var rows = await Match(query).Take(10).Select(p => new
        {
            p.Sku, p.Name, p.BaseUnit,
            OnHand = db.StockBalances.Where(b => b.ProductId == p.Id && whs.Contains(b.WarehouseId)).Sum(b => (decimal?)b.OnHand) ?? 0,
        }).ToListAsync(ct);
        return new DataTable($"Products matching “{query}”", ["sku", "name", "unit", "on_hand"],
            rows.Select(r => new object?[] { r.Sku, r.Name, r.BaseUnit, r.OnHand }).ToArray());
    }

    async Task<DataTable> StockByWarehouse(string query, string? warehouse, CancellationToken ct)
    {
        var whs = await Warehouses(warehouse, ct);
        var products = await Match(query).Take(5).Select(p => new { p.Id, p.Sku, p.Name, p.BaseUnit }).ToListAsync(ct);
        var ids = products.Select(p => p.Id).ToList();
        var incoming = await reorder.IncomingAsync(ct);
        var rows = await db.StockBalances.AsNoTracking().Where(b => ids.Contains(b.ProductId) && whs.Contains(b.WarehouseId))
            .Join(db.Warehouses, b => b.WarehouseId, w => w.Id, (b, w) => new { b.ProductId, b.WarehouseId, w.Code, b.OnHand, b.Reserved })
            .ToListAsync(ct);
        return new DataTable($"Stock of “{query}”", ["sku", "name", "warehouse", "on_hand", "reserved", "available", "on_order", "unit"],
            rows.OrderBy(r => products.FindIndex(p => p.Id == r.ProductId)).ThenBy(r => r.Code).Select(r =>
            {
                var p = products.First(x => x.Id == r.ProductId);
                return new object?[] { p.Sku, p.Name, r.Code, r.OnHand, r.Reserved, r.OnHand - r.Reserved,
                    incoming.GetValueOrDefault((r.ProductId, r.WarehouseId)), p.BaseUnit };
            }).ToArray());
    }

    async Task<DataTable> ReorderNeeded(string? warehouse, CancellationToken ct)
    {
        var whs = (await Warehouses(warehouse, ct)).ToHashSet();
        var list = (await reorder.SuggestAsync(false, ct)).Where(s => whs.Contains(s.WarehouseId)).ToList();
        return new DataTable("Needs reordering", ["sku", "name", "warehouse", "available", "on_order", "reorder_point", "suggested", "unit", "supplier"],
            list.Select(s => new object?[] { s.Sku, s.Name, s.Warehouse, s.OnHand - s.Reserved, s.Incoming, s.ReorderPoint,
                s.SuggestedQuantity, s.Unit, s.Supplier }).ToArray());
    }

    async Task<DataTable> Expiring(int days, string? warehouse, CancellationToken ct)
    {
        var whs = await Warehouses(warehouse, ct);
        var until = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(Math.Clamp(days, 1, 365));
        var rows = await db.BatchBalances.AsNoTracking().Where(b => b.OnHand > 0 && whs.Contains(b.WarehouseId))
            .Join(db.Batches, bb => bb.BatchId, b => b.Id, (bb, b) => new { bb, b })
            .Where(x => x.b.ExpiresOn != null && x.b.ExpiresOn <= until)
            .Join(db.Products, x => x.bb.ProductId, p => p.Id, (x, p) => new { p.Sku, p.Name, x.b.Number, x.b.ExpiresOn, x.bb.OnHand, x.bb.WarehouseId })
            .Join(db.Warehouses, x => x.WarehouseId, w => w.Id, (x, w) => new { x.Sku, x.Name, x.Number, x.ExpiresOn, x.OnHand, w.Code })
            .OrderBy(x => x.ExpiresOn).Take(100).ToListAsync(ct);
        return new DataTable($"Batches expiring within {days} days", ["sku", "name", "batch", "expires", "warehouse", "on_hand"],
            rows.Select(r => new object?[] { r.Sku, r.Name, r.Number, r.ExpiresOn?.ToString("yyyy-MM-dd"), r.Code, r.OnHand }).ToArray());
    }

    async Task<DataTable> Sales(string? from, string? to, string groupBy, int limit, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = DateOnly.TryParse(from, out var f) ? f : today.AddDays(-30);
        var end = DateOnly.TryParse(to, out var t) ? t : today;
        var (startAt, endAt) = (new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        var whs = await Warehouses(null, ct);
        var prices = Can(Perm.ViewSalesPrices);

        // Shipped quantities valued at the order line's net price (after discount, before tax).
        var rows = await (
            from sl in db.ShipmentLines.AsNoTracking()
            join s in db.Shipments on sl.ShipmentId equals s.Id
            join l in db.SalesOrderLines on sl.SalesOrderLineId equals l.Id
            where s.ShippedAt >= startAt && s.ShippedAt < endAt && whs.Contains(s.WarehouseId)
            select new { sl.ProductId, s.CustomerId, s.WarehouseId, s.ShippedAt, sl.QuantityBase, l.Factor, l.UnitPrice, l.DiscountPercent })
            .ToListAsync(ct);
        // Valued in memory: Postgres numeric division keeps more digits than a .NET decimal holds.
        var valued = rows.Select(r => new { r.ProductId, r.CustomerId, r.WarehouseId, r.ShippedAt, r.QuantityBase,
            Revenue = r.QuantityBase / r.Factor * r.UnitPrice * (1 - r.DiscountPercent / 100) }).ToList();

        var names = groupBy switch
        {
            "customer" => await db.Customers.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct),
            "warehouse" => await db.Warehouses.AsNoTracking().ToDictionaryAsync(w => w.Id, w => w.Code, ct),
            _ => await db.Products.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Sku + " " + p.Name, ct),
        };
        var grouped = valued.GroupBy(r => groupBy switch
            {
                "customer" => names[r.CustomerId],
                "warehouse" => names[r.WarehouseId],
                "month" => r.ShippedAt.ToString("yyyy-MM"),
                _ => names[r.ProductId],
            })
            .Select(g => new { Key = g.Key, Quantity = g.Sum(x => x.QuantityBase), Revenue = Math.Round(g.Sum(x => x.Revenue), 2) })
            .OrderByDescending(g => groupBy == "month" ? 0 : prices ? g.Revenue : g.Quantity).ThenBy(g => g.Key)
            .Take(Math.Clamp(limit, 1, 50)).ToList();
        var title = $"Sales {start:yyyy-MM-dd} to {end:yyyy-MM-dd} by {groupBy}";
        return prices
            ? new DataTable(title, [groupBy, "quantity", "revenue"], grouped.Select(g => new object?[] { g.Key, g.Quantity, g.Revenue }).ToArray())
            : new DataTable(title, [groupBy, "quantity"], grouped.Select(g => new object?[] { g.Key, g.Quantity }).ToArray());
    }

    async Task<DataTable> Velocity(string query, string? warehouse, CancellationToken ct)
    {
        var whs = await Warehouses(warehouse, ct);
        var products = await Match(query).Take(5).Select(p => new { p.Id, p.Sku, p.Name, p.BaseUnit }).ToListAsync(ct);
        var ids = products.Select(p => p.Id).ToList();
        var since = DateTimeOffset.UtcNow.AddDays(-90);
        var moves = await db.StockMovements.AsNoTracking()
            .Where(m => ids.Contains(m.ProductId) && whs.Contains(m.WarehouseId) && m.CreatedAt >= since && m.Quantity < 0)
            .Join(db.StockOperations.Where(o => o.Kind == Inventory.OperationKind.Shipment), m => m.OperationId, o => o.Id, (m, o) => new { m.ProductId, m.Quantity, o.CreatedAt })
            .ToListAsync(ct);
        var balances = await db.StockBalances.AsNoTracking().Where(b => ids.Contains(b.ProductId) && whs.Contains(b.WarehouseId))
            .GroupBy(b => b.ProductId).Select(g => new { g.Key, Available = g.Sum(b => b.OnHand - b.Reserved) }).ToDictionaryAsync(x => x.Key, x => x.Available, ct);
        var cut30 = DateTimeOffset.UtcNow.AddDays(-30);
        return new DataTable($"Sales speed of “{query}”", ["sku", "name", "per_day_30d", "per_day_90d", "available", "days_of_cover", "unit"],
            products.Select(p =>
            {
                var d30 = Math.Round(-moves.Where(m => m.ProductId == p.Id && m.CreatedAt >= cut30).Sum(m => m.Quantity) / 30m, 2);
                var d90 = Math.Round(-moves.Where(m => m.ProductId == p.Id).Sum(m => m.Quantity) / 90m, 2);
                var available = balances.GetValueOrDefault(p.Id);
                var rate = d30 > 0 ? d30 : d90;
                return new object?[] { p.Sku, p.Name, d30, d90, available, rate > 0 ? Math.Round(available / rate, 0) : null, p.BaseUnit };
            }).ToArray());
    }

    async Task<DataTable> Receivables(string? customer, CancellationToken ct)
    {
        if (!Can(Perm.ViewSalesPrices)) return Denied("Receivables");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var q = db.Invoices.AsNoTracking().Where(i => i.Status == InvoiceStatus.Open || i.Status == InvoiceStatus.PartiallyPaid)
            .Join(db.Customers, i => i.CustomerId, c => c.Id, (i, c) => new { c.Name, Balance = i.Total - i.AmountPaid, i.DueDate });
        if (customer is { } name) q = q.Where(x => EF.Functions.ILike(x.Name, $"%{name}%"));
        var open = await q.ToListAsync(ct);
        var rows = open.GroupBy(x => x.Name).Select(g =>
        {
            decimal B(int a, int b) => g.Where(x => today.DayNumber - x.DueDate.DayNumber is var d && d >= a && d <= b).Sum(x => x.Balance);
            return new object?[] { g.Key, B(int.MinValue, 0), B(1, 30), B(31, 60), B(61, int.MaxValue), g.Sum(x => x.Balance) };
        }).OrderByDescending(r => (decimal)r[5]!).ToArray();
        return new DataTable("Receivables by age", ["customer", "not_due", "overdue_1_30", "overdue_31_60", "overdue_60_plus", "total"], rows);
    }

    async Task<DataTable> OpenPurchaseOrders(string? supplier, bool overdueOnly, CancellationToken ct)
    {
        var allowed = await access.AllowedAsync(ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var q = db.PurchaseOrders.AsNoTracking()
            .Where(p => (p.Status == PoStatus.Approved || p.Status == PoStatus.Sent || p.Status == PoStatus.PartiallyReceived)
                        && (allowed == null || allowed.Contains(p.WarehouseId)));
        if (overdueOnly) q = q.Where(p => p.ExpectedOn != null && p.ExpectedOn < today);
        var rows = await q.Select(p => new
        {
            p.Number, p.Status, p.ExpectedOn,
            Supplier = db.Suppliers.Where(s => s.Id == p.SupplierId).Select(s => s.Name).First(),
            Warehouse = db.Warehouses.Where(w => w.Id == p.WarehouseId).Select(w => w.Code).First(),
            Ordered = p.Lines.Sum(l => l.Quantity * l.Factor), Received = p.Lines.Sum(l => l.ReceivedBase),
        }).ToListAsync(ct);
        if (supplier is { } s) rows = rows.Where(r => r.Supplier.Contains(s, StringComparison.OrdinalIgnoreCase)).ToList();
        return new DataTable("Open purchase orders", ["number", "supplier", "warehouse", "status", "expected", "received_percent"],
            rows.OrderBy(r => r.ExpectedOn).Select(r => new object?[] { r.Number, r.Supplier, r.Warehouse, r.Status.ToString(),
                r.ExpectedOn?.ToString("yyyy-MM-dd"), r.Ordered == 0 ? 0 : Math.Round(r.Received / r.Ordered * 100) }).ToArray());
    }
}
