using Microsoft.EntityFrameworkCore;
using Npgsql;
using Tallyard.Api.Common;
using Tallyard.Api.Infrastructure;
using Tallyard.Api.Modules.Catalog;
using Tallyard.Api.Modules.Warehouses;

namespace Tallyard.Api.Modules.Inventory;

/// <summary>A change to post, in base units: positive into the warehouse, negative out of it.</summary>
/// <param name="BatchId">Required for stock coming into a batch-tracked product. For stock going
/// out, null means "pick by earliest expiry" (FEFO).</param>
/// <param name="UnitCost">Purchase cost for incoming stock; null keeps the current average.</param>
/// <param name="FromReserved">Outgoing stock that was reserved for this purpose (a shipment of a
/// confirmed order): it uses up the reservation instead of requiring free stock.</param>
public record StockChange(
    Guid ProductId, Guid WarehouseId, decimal Quantity,
    Guid? BatchId = null, decimal? UnitCost = null, bool FromReserved = false);

public record PostRequest(
    OperationKind Kind,
    string NumberPrefix,
    IReadOnlyList<StockChange> Changes,
    string? IdempotencyKey = null,
    string? Reason = null,
    string? Note = null,
    string? SourceType = null,
    Guid? SourceId = null,
    /// <summary>Use this document number instead of taking a new one (a transfer's movements carry the transfer's number).</summary>
    string? Number = null);

/// <summary>Sent to the browsers of the tenant after a posting commits.</summary>
public record BalanceChanged(Guid ProductId, Guid WarehouseId, decimal OnHand, decimal Reserved);

/// <summary>
/// The only code that changes stock. Each posting, in one transaction:
/// <list type="number">
/// <item>locks the balance rows it touches, always in the same order (product, then warehouse) so
/// two postings can't deadlock;</item>
/// <item>checks there is enough free stock, picking batches by earliest expiry when needed;</item>
/// <item>writes append-only movements and updates the balances and the moving average cost.</item>
/// </list>
/// The database's CHECK constraints (no negative on-hand, reserved within on-hand) back this up:
/// a bug here fails the transaction instead of corrupting stock.
/// </summary>
public sealed class StockLedger(AppDbContext db, CurrentUser user, DocumentNumbers numbers, IStockNotifier notifier)
{
    readonly List<BalanceChanged> pending = [];

    public async Task<StockOperation> PostAsync(PostRequest req, CancellationToken ct)
    {
        if (req.Changes.Count == 0) throw new ArgumentException("Nothing to post", nameof(req));
        if (req.Changes.Any(c => c.Quantity == 0)) throw new ArgumentException("Zero quantity", nameof(req));

        if (req.IdempotencyKey is { } key && await Existing(key, ct) is { } done) return done;

        var owns = db.Database.CurrentTransaction is null;
        await using var tx = owns ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            var op = await Post(req, ct);
            if (owns)
            {
                await tx!.CommitAsync(ct);
                await PublishAsync(ct);
            }
            return op;
        }
        catch (DbUpdateException e) when (owns && req.IdempotencyKey is { } k && IsUniqueViolation(e))
        {
            // The same request was posted concurrently and won the race: return its result.
            await tx!.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return (await Existing(k, ct))!;
        }
    }

    /// <summary>
    /// Promises stock to orders: for each (product, warehouse, wanted quantity), reserves as much
    /// as is free, up to the wanted quantity, and returns what it got. With
    /// <paramref name="allOrNothing"/>, any shortfall throws instead and nothing is reserved.
    /// Must run inside the caller's transaction (the order and its reservation commit together).
    /// </summary>
    public async Task<Dictionary<(Guid ProductId, Guid WarehouseId), decimal>> ReserveAsync(
        IReadOnlyList<(Guid ProductId, Guid WarehouseId, decimal Quantity)> wanted, bool allOrNothing, CancellationToken ct)
    {
        RequireTransaction();
        var got = new Dictionary<(Guid, Guid), decimal>();
        foreach (var g in wanted.GroupBy(w => (w.ProductId, w.WarehouseId)).OrderBy(g => g.Key.ProductId).ThenBy(g => g.Key.WarehouseId))
        {
            var need = g.Sum(x => x.Quantity);
            if (need <= 0) continue;
            var bal = await LockBalance(g.Key.ProductId, g.Key.WarehouseId, ct);
            var take = Math.Min(need, Math.Max(0, bal.Available));
            if (take < need && allOrNothing)
            {
                var product = await db.Products.AsNoTracking().FirstAsync(p => p.Id == g.Key.ProductId, ct);
                var warehouse = await db.Warehouses.AsNoTracking().FirstAsync(w => w.Id == g.Key.WarehouseId, ct);
                throw new AppException(409, "insufficient_stock", ("sku", product.Sku), ("warehouse", warehouse.Code),
                    ("available", Math.Max(0, bal.Available).ToString("0.####")), ("unit", product.BaseUnit));
            }
            bal.Reserved += take;
            bal.UpdatedAt = DateTimeOffset.UtcNow;
            got[g.Key] = take;
            pending.Add(new BalanceChanged(bal.ProductId, bal.WarehouseId, bal.OnHand, bal.Reserved));
        }
        await db.SaveChangesAsync(ct);
        return got;
    }

    /// <summary>Gives reserved stock back (an order cancelled or closed short).</summary>
    public async Task ReleaseAsync(IReadOnlyList<(Guid ProductId, Guid WarehouseId, decimal Quantity)> released, CancellationToken ct)
    {
        RequireTransaction();
        foreach (var g in released.GroupBy(w => (w.ProductId, w.WarehouseId)).OrderBy(g => g.Key.ProductId).ThenBy(g => g.Key.WarehouseId))
        {
            var qty = g.Sum(x => x.Quantity);
            if (qty <= 0) continue;
            var bal = await LockBalance(g.Key.ProductId, g.Key.WarehouseId, ct);
            if (bal.Reserved < qty) throw new InvalidOperationException("Releasing more than is reserved");
            bal.Reserved -= qty;
            bal.UpdatedAt = DateTimeOffset.UtcNow;
            pending.Add(new BalanceChanged(bal.ProductId, bal.WarehouseId, bal.OnHand, bal.Reserved));
        }
        await db.SaveChangesAsync(ct);
    }

    void RequireTransaction()
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Reservations must run inside a transaction");
    }

    /// <summary>For callers that post inside their own transaction: send the live updates after committing.</summary>
    public async Task PublishAsync(CancellationToken ct)
    {
        if (pending.Count == 0) return;
        var changes = pending.GroupBy(c => (c.ProductId, c.WarehouseId)).Select(g => g.Last()).ToList();
        pending.Clear();
        await notifier.BalancesChangedAsync(user.RequiredTenantId, changes, ct);
    }

    async Task<StockOperation> Post(PostRequest req, CancellationToken ct)
    {
        var productIds = req.Changes.Select(c => c.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);
        if (products.Count != productIds.Count) throw AppException.NotFound("product_not_found");
        var warehouseIds = req.Changes.Select(c => c.WarehouseId).Distinct().ToList();
        var warehouses = await db.Warehouses.AsNoTracking().Where(w => warehouseIds.Contains(w.Id))
            .ToDictionaryAsync(w => w.Id, ct);
        if (warehouses.Count != warehouseIds.Count) throw AppException.NotFound("warehouse_not_found");

        var op = new StockOperation
        {
            Kind = req.Kind,
            Number = req.Number ?? await numbers.NextAsync(req.NumberPrefix, ct),
            IdempotencyKey = req.IdempotencyKey,
            Reason = req.Reason,
            Note = req.Note,
            SourceType = req.SourceType,
            SourceId = req.SourceId,
            CreatedBy = user.UserId,
        };
        db.StockOperations.Add(op);

        // Lock every balance this posting touches, in a fixed order.
        var balances = new Dictionary<(Guid, Guid), StockBalance>();
        foreach (var (p, w) in req.Changes.Select(c => (c.ProductId, c.WarehouseId)).Distinct().OrderBy(k => k.ProductId).ThenBy(k => k.WarehouseId))
            balances[(p, w)] = await LockBalance(p, w, ct);

        var now = DateTimeOffset.UtcNow;
        foreach (var change in req.Changes)
        {
            var product = products[change.ProductId];
            var bal = balances[(change.ProductId, change.WarehouseId)];
            if (change.Quantity > 0) await In(op, product, bal, change, ct);
            else await Out(op, product, warehouses[change.WarehouseId], bal, change, ct);
            bal.UpdatedAt = now;
            pending.Add(new BalanceChanged(bal.ProductId, bal.WarehouseId, bal.OnHand, bal.Reserved));
        }

        await db.SaveChangesAsync(ct);
        return op;
    }

    async Task In(StockOperation op, Product product, StockBalance bal, StockChange c, CancellationToken ct)
    {
        if (product.TrackBatches && c.BatchId is null) throw new AppException(422, "batch_required", ("sku", product.Sku));
        if (!product.TrackBatches && c.BatchId is not null) throw new AppException(422, "batch_not_tracked", ("sku", product.Sku));

        // Moving average: the new stock is valued at its purchase cost; without one it keeps the average.
        var cost = c.UnitCost ?? bal.AvgCost;
        var total = bal.OnHand + c.Quantity;
        bal.AvgCost = Math.Round((bal.OnHand * bal.AvgCost + c.Quantity * cost) / total, 4);
        bal.OnHand = total;

        if (c.BatchId is { } batchId)
        {
            var bb = await LockBatchBalance(c.ProductId, c.WarehouseId, batchId, ct);
            bb.OnHand += c.Quantity;
        }
        op.Movements.Add(Movement(c, c.BatchId, c.Quantity, cost, bal.OnHand));
    }

    async Task Out(StockOperation op, Product product, Warehouse warehouse, StockBalance bal, StockChange c, CancellationToken ct)
    {
        var qty = -c.Quantity;
        var free = c.FromReserved ? bal.OnHand : bal.Available;
        if (free < qty)
            throw new AppException(409, "insufficient_stock",
                ("sku", product.Sku), ("warehouse", warehouse.Code), ("available", Math.Max(0, free).ToString("0.####")), ("unit", product.BaseUnit));
        if (c.FromReserved)
        {
            if (bal.Reserved < qty) throw new InvalidOperationException("Shipping more than was reserved");
            bal.Reserved -= qty;
        }

        if (!product.TrackBatches)
        {
            bal.OnHand -= qty;
            op.Movements.Add(Movement(c, null, -qty, bal.AvgCost, bal.OnHand));
            return;
        }

        // Batch-tracked: a named batch, or earliest expiry first (FEFO).
        foreach (var (batchId, take) in await Allocate(c, qty, product, warehouse, ct))
        {
            bal.OnHand -= take;
            op.Movements.Add(Movement(c, batchId, -take, bal.AvgCost, bal.OnHand));
        }
    }

    async Task<List<(Guid BatchId, decimal Qty)>> Allocate(StockChange c, decimal qty, Product product, Warehouse warehouse, CancellationToken ct)
    {
        var rows = await db.BatchBalances.FromSql($"""
                SELECT bb.* FROM batch_balances bb JOIN batches b ON b.id = bb.batch_id
                WHERE bb.product_id = {c.ProductId} AND bb.warehouse_id = {c.WarehouseId} AND bb.on_hand > 0
                ORDER BY b.expires_on NULLS LAST, b.created_at
                FOR UPDATE OF bb
                """)
            .AsAsyncEnumerable().ToListAsync(ct);
        if (c.BatchId is { } only) rows = rows.Where(r => r.BatchId == only).ToList();

        var picks = new List<(Guid, decimal)>();
        var left = qty;
        foreach (var row in rows)
        {
            if (left == 0) break;
            var take = Math.Min(row.OnHand, left);
            row.OnHand -= take;
            left -= take;
            picks.Add((row.BatchId, take));
        }
        if (left > 0)
            throw new AppException(409, "insufficient_stock",
                ("sku", product.Sku), ("warehouse", warehouse.Code), ("available", (qty - left).ToString("0.####")), ("unit", product.BaseUnit));
        return picks;
    }

    StockMovement Movement(StockChange c, Guid? batchId, decimal qty, decimal cost, decimal after) => new()
    {
        ProductId = c.ProductId,
        WarehouseId = c.WarehouseId,
        BatchId = batchId,
        Quantity = qty,
        UnitCost = cost,
        BalanceAfter = after,
    };

    async Task<StockBalance> LockBalance(Guid productId, Guid warehouseId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO stock_balances (tenant_id, product_id, warehouse_id, on_hand, reserved, avg_cost, updated_at)
            VALUES ({user.RequiredTenantId}, {productId}, {warehouseId}, 0, 0, 0, now())
            ON CONFLICT (product_id, warehouse_id) DO NOTHING
            """, ct);
        Forget<StockBalance>(b => b.ProductId == productId && b.WarehouseId == warehouseId);
        return await db.StockBalances
            .FromSql($"SELECT * FROM stock_balances WHERE product_id = {productId} AND warehouse_id = {warehouseId} FOR UPDATE")
            .AsAsyncEnumerable().SingleAsync(ct); // FOR UPDATE can't be composed into a subquery
    }

    async Task<BatchBalance> LockBatchBalance(Guid productId, Guid warehouseId, Guid batchId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO batch_balances (tenant_id, product_id, warehouse_id, batch_id, on_hand)
            VALUES ({user.RequiredTenantId}, {productId}, {warehouseId}, {batchId}, 0)
            ON CONFLICT (warehouse_id, batch_id) DO NOTHING
            """, ct);
        Forget<BatchBalance>(b => b.WarehouseId == warehouseId && b.BatchId == batchId);
        return await db.BatchBalances
            .FromSql($"SELECT * FROM batch_balances WHERE warehouse_id = {warehouseId} AND batch_id = {batchId} FOR UPDATE")
            .AsAsyncEnumerable().SingleAsync(ct);
    }

    // A row this context loaded earlier would be returned as-is, with values from before we
    // waited for the lock. Detach it so the locked read is the one that counts.
    void Forget<T>(Func<T, bool> match) where T : class
    {
        foreach (var e in db.ChangeTracker.Entries<T>().Where(e => match(e.Entity)).ToList())
            e.State = EntityState.Detached;
    }

    async Task<StockOperation?> Existing(string key, CancellationToken ct) =>
        await db.StockOperations.AsNoTracking().Include(o => o.Movements)
            .FirstOrDefaultAsync(o => o.IdempotencyKey == key, ct);

    static bool IsUniqueViolation(DbUpdateException e) =>
        e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>The batch with this number, created if new. Expiry is kept from the first receipt.</summary>
    public async Task<Batch> BatchAsync(Guid productId, string number, DateOnly? expiresOn, CancellationToken ct)
    {
        number = number.Trim().ToUpperInvariant();
        var batch = await db.Batches.FirstOrDefaultAsync(b => b.ProductId == productId && b.Number == number, ct);
        if (batch is not null)
        {
            if (expiresOn is { } e && batch.ExpiresOn != e)
                throw new AppException(422, "batch_expiry_mismatch", ("batch", number), ("expiry", batch.ExpiresOn?.ToString("yyyy-MM-dd") ?? "-"));
            return batch;
        }
        batch = new Batch { ProductId = productId, Number = number, ExpiresOn = expiresOn };
        db.Batches.Add(batch);
        await db.SaveChangesAsync(ct);
        return batch;
    }
}
