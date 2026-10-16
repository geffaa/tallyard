using Microsoft.EntityFrameworkCore;
using Tallyard.Api.Common;
using Tallyard.Api.Infrastructure;

namespace Tallyard.Api.Modules.Purchasing;

public record ReorderSuggestion(
    Guid ProductId, string Sku, string Name, string BaseUnit, Guid WarehouseId, string Warehouse,
    Guid SupplierId, string Supplier, decimal OnHand, decimal Reserved, decimal Incoming, decimal ReorderPoint,
    decimal SuggestedBase, string Unit, decimal Factor, decimal SuggestedQuantity, decimal? UnitPrice);

public record ReorderPick(Guid ProductId, Guid WarehouseId, decimal Quantity);

/// <summary>
/// What to order now: products whose available stock plus what is already on order has fallen to
/// the reorder point. Computed on request from live balances, so it is never stale.
/// </summary>
public sealed class Reorder(AppDbContext db, WarehouseAccess access)
{
    static readonly PoStatus[] Open = [PoStatus.Approved, PoStatus.Sent, PoStatus.PartiallyReceived];

    /// <summary>Base units ordered but not yet received, per product and warehouse.</summary>
    public async Task<Dictionary<(Guid ProductId, Guid WarehouseId), decimal>> IncomingAsync(CancellationToken ct) =>
        (await (from l in db.PurchaseOrderLines.AsNoTracking()
                join po in db.PurchaseOrders on l.PurchaseOrderId equals po.Id
                where Open.Contains(po.Status) && l.Quantity * l.Factor > l.ReceivedBase
                group l by new { l.ProductId, po.WarehouseId } into g
                select new { g.Key.ProductId, g.Key.WarehouseId, Quantity = g.Sum(l => l.Quantity * l.Factor - l.ReceivedBase) })
            .ToListAsync(ct))
        .ToDictionary(x => (x.ProductId, x.WarehouseId), x => x.Quantity);

    public async Task<List<ReorderSuggestion>> SuggestAsync(bool withPrices, CancellationToken ct)
    {
        var allowed = await access.AllowedAsync(ct);
        var incoming = await IncomingAsync(ct);
        // A few hundred rules per warehouse: load them with their balances, then compare in memory.
        var candidates = await (
            from r in db.ReorderRules.AsNoTracking()
            where allowed == null || allowed.Contains(r.WarehouseId)
            join p in db.Products on r.ProductId equals p.Id
            where p.IsActive
            join w in db.Warehouses on r.WarehouseId equals w.Id
            join s in db.Suppliers on r.SupplierId equals s.Id
            from b in db.StockBalances.Where(b => b.ProductId == r.ProductId && b.WarehouseId == r.WarehouseId).DefaultIfEmpty()
            select new
            {
                r.ProductId, p.Sku, p.Name, p.BaseUnit, r.WarehouseId, Warehouse = w.Code, r.SupplierId, Supplier = s.Name,
                OnHand = b == null ? 0 : b.OnHand, Reserved = b == null ? 0 : b.Reserved, r.ReorderPoint, r.ReorderQuantity,
                AvgCost = b == null ? 0 : b.AvgCost,
            }).ToListAsync(ct);
        var rows = candidates
            .Select(c => new { c, Incoming = incoming.GetValueOrDefault((c.ProductId, c.WarehouseId)) })
            .Where(x => x.c.OnHand - x.c.Reserved + x.Incoming <= x.c.ReorderPoint)
            .OrderBy(x => x.c.Supplier).ThenBy(x => x.c.Sku)
            .Select(x => new
            {
                x.c.ProductId, x.c.Sku, x.c.Name, x.c.BaseUnit, x.c.WarehouseId, x.c.Warehouse, x.c.SupplierId, x.c.Supplier,
                x.c.OnHand, x.c.Reserved, x.Incoming, x.c.ReorderPoint, x.c.ReorderQuantity, x.c.AvgCost,
            }).ToList();

        var productIds = rows.Select(r => r.ProductId).Distinct().ToList();
        // Order in the product's largest pack when there is one (suppliers ship cases).
        var packs = await db.ProductUnits.AsNoTracking().Where(u => productIds.Contains(u.ProductId))
            .GroupBy(u => u.ProductId).Select(g => g.OrderByDescending(u => u.Factor).First()).ToDictionaryAsync(u => u.ProductId, ct);
        var lastPrices = withPrices ? await LastPrices(productIds, ct) : [];

        return rows.Select(r =>
        {
            var pack = packs.GetValueOrDefault(r.ProductId);
            var (unit, factor) = pack is null ? (r.BaseUnit, 1m) : (pack.Name, pack.Factor);
            // Enough to get back above the reorder point by the reorder quantity, in whole packs.
            var shortfall = r.ReorderPoint - (r.OnHand - r.Reserved + r.Incoming);
            var need = Math.Max(r.ReorderQuantity, shortfall + r.ReorderQuantity);
            var packsNeeded = Math.Ceiling(need / factor);
            decimal? price = withPrices
                ? lastPrices.TryGetValue((r.ProductId, r.SupplierId), out var last) ? last * factor : Math.Round(r.AvgCost * factor, 4)
                : null;
            return new ReorderSuggestion(r.ProductId, r.Sku, r.Name, r.BaseUnit, r.WarehouseId, r.Warehouse, r.SupplierId, r.Supplier,
                r.OnHand, r.Reserved, r.Incoming, r.ReorderPoint, packsNeeded * factor, unit, factor, packsNeeded, price);
        }).ToList();
    }

    /// <summary>The last price paid per base unit, per product and supplier.</summary>
    async Task<Dictionary<(Guid, Guid), decimal>> LastPrices(List<Guid> productIds, CancellationToken ct)
    {
        var rows = await (
            from l in db.PurchaseOrderLines.AsNoTracking()
            join po in db.PurchaseOrders on l.PurchaseOrderId equals po.Id
            where productIds.Contains(l.ProductId) && po.Status != PoStatus.Cancelled && po.Status != PoStatus.Draft
            orderby po.OrderDate descending, po.CreatedAt descending
            select new { l.ProductId, po.SupplierId, l.UnitPrice, l.Factor }).ToListAsync(ct);
        return rows.GroupBy(r => (r.ProductId, r.SupplierId)).ToDictionary(g => g.Key, g => g.First().UnitPrice / g.First().Factor);
    }

    /// <summary>Turns the picked suggestions into one draft order per supplier and warehouse.</summary>
    public async Task<List<PurchaseOrder>> CreateDraftsAsync(ReorderPick[] picks, PurchasingService purchasing, CancellationToken ct)
    {
        var suggestions = (await SuggestAsync(true, ct)).ToDictionary(s => (s.ProductId, s.WarehouseId));
        var created = new List<PurchaseOrder>();
        foreach (var group in picks.GroupBy(p => (suggestions.GetValueOrDefault((p.ProductId, p.WarehouseId))?.SupplierId, p.WarehouseId)))
        {
            if (group.Key.SupplierId is not { } supplierId) throw new AppException(409, "reorder_stale");
            var lines = group.Select(p =>
            {
                var s = suggestions[(p.ProductId, p.WarehouseId)];
                return new PoLineInput(p.ProductId, p.Quantity, s.Unit, s.UnitPrice ?? 0, null);
            }).ToArray();
            created.Add(await purchasing.CreateAsync(new PoInput(supplierId, group.Key.WarehouseId, null, null, lines, null), ct));
        }
        return created;
    }
}

