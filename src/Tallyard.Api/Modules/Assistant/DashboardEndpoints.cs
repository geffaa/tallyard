using Microsoft.EntityFrameworkCore;
using Tallyard.Api.Infrastructure;
using Tallyard.Api.Modules.Purchasing;
using Tallyard.Api.Modules.Sales;

namespace Tallyard.Api.Modules.Assistant;

public record DashboardTile(string Key, decimal Value, string Kind, string? Link);
public record DailyPoint(DateOnly Day, decimal Revenue, decimal Units);
public record TopItem(string Label, decimal Value);
public record Dashboard(DashboardTile[] Tiles, DailyPoint[] Sales, TopItem[] TopProducts, string ValueKind);

/// <summary>
/// The home screen: what needs attention today, for this person's role and warehouses. Each tile
/// links to the screen where it is dealt with. Money is left out for roles that can't see it.
/// </summary>
public static class DashboardEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/dashboard", async (AppDbContext db, CurrentUser me, WarehouseAccess access, Reorder reorder, CancellationToken ct) =>
        {
            var role = me.Role!.Value;
            var allowed = await access.AllowedAsync(ct);
            var whs = await db.Warehouses.AsNoTracking().Where(w => w.IsActive && (allowed == null || allowed.Contains(w.Id))).Select(w => w.Id).ToListAsync(ct);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var tiles = new List<DashboardTile>();
            bool can(string p) => Perm.Has(role, p);

            var reorderCount = (await reorder.SuggestAsync(false, ct)).Count(s => whs.Contains(s.WarehouseId));
            tiles.Add(new("reorder", reorderCount, "count", can(Perm.ViewPurchasing) ? "/purchasing/reorder" : "/stock"));
            var until = today.AddDays(30);
            var expiring = await db.BatchBalances.AsNoTracking().Where(b => b.OnHand > 0 && whs.Contains(b.WarehouseId))
                .Join(db.Batches, bb => bb.BatchId, b => b.Id, (bb, b) => b.ExpiresOn).CountAsync(e => e != null && e <= until, ct);
            tiles.Add(new("expiring", expiring, "count", "/assistant"));
            var outOfStock = await db.StockBalances.AsNoTracking().CountAsync(b => whs.Contains(b.WarehouseId) && b.OnHand - b.Reserved <= 0, ct);
            tiles.Add(new("out_of_stock", outOfStock, "count", "/stock"));
            if (can(Perm.ViewCosts))
                tiles.Add(new("stock_value", Math.Round(await db.StockBalances.AsNoTracking().Where(b => whs.Contains(b.WarehouseId))
                    .SumAsync(b => (decimal?)(b.OnHand * b.AvgCost), ct) ?? 0, 2), "money", "/stock"));

            if (can(Perm.ViewPurchasing))
            {
                var pending = await db.PurchaseOrders.AsNoTracking().CountAsync(p => p.Status == PoStatus.PendingApproval && whs.Contains(p.WarehouseId), ct);
                tiles.Add(new("po_approval", pending, "count", "/purchasing"));
                var late = await db.PurchaseOrders.AsNoTracking().CountAsync(p => (p.Status == PoStatus.Sent || p.Status == PoStatus.Approved || p.Status == PoStatus.PartiallyReceived)
                    && p.ExpectedOn != null && p.ExpectedOn < today && whs.Contains(p.WarehouseId), ct);
                tiles.Add(new("po_late", late, "count", "/purchasing"));
            }
            if (can(Perm.ViewSales))
            {
                var toShip = await db.SalesOrders.AsNoTracking().CountAsync(o => (o.Status == SoStatus.Confirmed || o.Status == SoStatus.PartiallyShipped)
                    && o.Lines.Any(l => l.ReservedBase > 0) && whs.Contains(o.WarehouseId), ct);
                tiles.Add(new("to_ship", toShip, "count", "/sales"));
                var held = await db.SalesOrders.AsNoTracking().CountAsync(o => o.Status == SoStatus.OnHold && whs.Contains(o.WarehouseId), ct);
                tiles.Add(new("on_hold", held, "count", "/sales"));
            }
            if (can(Perm.ViewSalesPrices))
            {
                var overdue = await db.Invoices.AsNoTracking().Where(i => (i.Status == InvoiceStatus.Open || i.Status == InvoiceStatus.PartiallyPaid) && i.DueDate < today)
                    .SumAsync(i => (decimal?)(i.Total - i.AmountPaid), ct) ?? 0;
                tiles.Add(new("overdue_receivables", overdue, "money", "/receivables"));
            }

            // Shipped per day over 30 days: revenue for those who may see it, units otherwise.
            // UTC midnight: .Date alone gives a DateTime that converts with the server's local offset.
            var since = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-29), TimeSpan.Zero);
            var shipped = await (
                from sl in db.ShipmentLines.AsNoTracking()
                join s in db.Shipments on sl.ShipmentId equals s.Id
                join l in db.SalesOrderLines on sl.SalesOrderLineId equals l.Id
                where s.ShippedAt >= since && whs.Contains(s.WarehouseId)
                select new { s.ShippedAt, sl.ProductId, sl.QuantityBase, l.Factor, l.UnitPrice, l.DiscountPercent }).ToListAsync(ct);
            var prices = can(Perm.ViewSalesPrices);
            var valued = shipped.Select(x => new { Day = DateOnly.FromDateTime(x.ShippedAt.UtcDateTime), x.ProductId, x.QuantityBase,
                Revenue = x.QuantityBase / x.Factor * x.UnitPrice * (1 - x.DiscountPercent / 100) }).ToList();
            var days = Enumerable.Range(0, 30).Select(i => DateOnly.FromDateTime(since.AddDays(i).Date)).Select(d =>
                new DailyPoint(d, prices ? Math.Round(valued.Where(v => v.Day == d).Sum(v => v.Revenue), 2) : 0, valued.Where(v => v.Day == d).Sum(v => v.QuantityBase)))
                .ToArray();
            var names = await db.Products.AsNoTracking().Where(p => valued.Select(v => v.ProductId).Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
            var top = valued.GroupBy(v => v.ProductId)
                .Select(g => new TopItem(names[g.Key], Math.Round(prices ? g.Sum(v => v.Revenue) : g.Sum(v => v.QuantityBase), 2)))
                .OrderByDescending(t => t.Value).Take(6).ToArray();
            return new Dashboard(tiles.ToArray(), days, top, prices ? "money" : "units");
        });
    }
}
