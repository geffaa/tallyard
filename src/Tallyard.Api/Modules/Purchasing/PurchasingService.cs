using Microsoft.EntityFrameworkCore;
using Tallyard.Api.Common;
using Tallyard.Api.Infrastructure;
using Tallyard.Api.Modules.Inventory;

namespace Tallyard.Api.Modules.Purchasing;

public record PoLineInput(Guid ProductId, decimal Quantity, string? Unit, decimal UnitPrice, decimal? TaxRate);
public record PoInput(Guid SupplierId, Guid WarehouseId, DateOnly? ExpectedOn, string? Note, PoLineInput[] Lines, uint? Version);
public record ReceiveLineInput(Guid LineId, decimal Quantity, string? BatchNumber, DateOnly? ExpiresOn);
public record ReceiveInput(ReceiveLineInput[] Lines, string? Note, string? IdempotencyKey);

/// <summary>
/// Purchasing use cases. The rules live on <see cref="PurchaseOrder"/>; this class loads,
/// locks, saves and talks to the stock ledger. Endpoints and the demo seeder both use it.
/// </summary>
public sealed class PurchasingService(
    AppDbContext db, CurrentUser me, DocumentNumbers numbers, StockLedger ledger, Quantities qty, WarehouseAccess access)
{
    /// <summary>Received quantities may exceed the order by this much (suppliers round up to full packs).</summary>
    public const decimal OverReceiptTolerance = 0.05m;

    public async Task<PurchaseOrder> CreateAsync(PoInput input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var po = new PurchaseOrder { Number = await numbers.NextAsync("PO", ct), CreatedBy = me.UserId };
        await Apply(po, input, ct);
        db.PurchaseOrders.Add(po);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return po;
    }

    public async Task<PurchaseOrder> UpdateAsync(Guid id, PoInput input, CancellationToken ct)
    {
        var po = await Load(id, ct);
        if (!po.Editable) throw AppException.Conflict("po_not_draft");
        db.Entry(po).Property(x => x.Version).OriginalValue = input.Version ?? po.Version;
        await Apply(po, input, ct);
        await db.SaveChangesAsync(ct);
        return po;
    }

    async Task Apply(PurchaseOrder po, PoInput input, CancellationToken ct)
    {
        if (!await db.Suppliers.AnyAsync(s => s.Id == input.SupplierId && s.IsActive, ct)) throw AppException.NotFound("supplier_not_found");
        if (!await db.Warehouses.AnyAsync(w => w.Id == input.WarehouseId && w.IsActive, ct)) throw AppException.NotFound("warehouse_not_found");
        po.SupplierId = input.SupplierId;
        po.WarehouseId = input.WarehouseId;
        po.ExpectedOn = input.ExpectedOn;
        po.Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();

        // Replace the lines (a draft has no receipts, so nothing refers to them yet).
        db.PurchaseOrderLines.RemoveRange(po.Lines);
        po.Lines.Clear();
        var position = 0;
        foreach (var l in input.Lines)
        {
            var product = await qty.ProductAsync(l.ProductId, ct);
            var unit = string.IsNullOrWhiteSpace(l.Unit) ? product.BaseUnit : l.Unit.Trim().ToLowerInvariant();
            var factor = await qty.ToBaseAsync(l.ProductId, 1, unit, ct);
            po.Lines.Add(new PurchaseOrderLine
            {
                PurchaseOrderId = po.Id, ProductId = l.ProductId, Unit = unit, Factor = factor, Quantity = l.Quantity,
                UnitPrice = l.UnitPrice, TaxRate = l.TaxRate ?? product.TaxRate, Position = position++,
            });
        }
        po.Recalculate();
    }

    public async Task<PurchaseOrder> SubmitAsync(Guid id, CancellationToken ct)
    {
        var po = await Load(id, ct);
        po.Submit(await Rules(ct), DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        return po;
    }

    public async Task<PurchaseOrder> ApproveAsync(Guid id, string? comment, CancellationToken ct)
    {
        var po = await Load(id, ct);
        po.ApproveLevel(await Rules(ct), me.RequiredUserId, me.Role!.Value, comment, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        return po;
    }

    public async Task<PurchaseOrder> RejectAsync(Guid id, string comment, CancellationToken ct)
    {
        var po = await Load(id, ct);
        var rules = await Rules(ct);
        var next = PurchaseOrder.Required(po.Total, rules).FirstOrDefault(r => po.Approvals.All(a => a.Level != r.Level));
        if (next is not null && me.Role != next.Role && me.Role != Identity.Role.Owner)
            throw new AppException(403, "po_wrong_approver", ("role", next.Role.ToString()));
        po.Reject(me.RequiredUserId, comment, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        return po;
    }

    public async Task<PurchaseOrder> TransitionAsync(Guid id, Action<PurchaseOrder, DateTimeOffset> change, CancellationToken ct)
    {
        var po = await Load(id, ct);
        change(po, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        return po;
    }

    /// <summary>
    /// Receives goods against an order. The order row is locked first, so two people receiving
    /// the same order at once are serialized and can't together receive more than was ordered.
    /// </summary>
    public async Task<GoodsReceipt> ReceiveAsync(Guid id, ReceiveInput input, CancellationToken ct)
    {
        if (input.IdempotencyKey is { } key &&
            await db.StockOperations.AsNoTracking().FirstOrDefaultAsync(o => o.IdempotencyKey == key, ct) is { SourceId: { } done })
            return await db.GoodsReceipts.AsNoTracking().Include(g => g.Lines).FirstAsync(g => g.Id == done, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var po = await db.PurchaseOrders.FromSql($"SELECT *, xmin FROM purchase_orders WHERE id = {id} FOR UPDATE")
                     .AsAsyncEnumerable().FirstOrDefaultAsync(ct)
                 ?? throw AppException.NotFound("po_not_found");
        await db.Entry(po).Collection(p => p.Lines).LoadAsync(ct);
        await access.EnsureAsync(po.WarehouseId, ct);
        if (!po.Receivable) throw AppException.Conflict("po_not_receivable");

        var receipt = new GoodsReceipt
        {
            Number = await numbers.NextAsync("GRN", ct), PurchaseOrderId = po.Id, WarehouseId = po.WarehouseId,
            ReceivedBy = me.UserId, Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim(),
        };
        var changes = new List<StockChange>();
        foreach (var l in input.Lines.Where(l => l.Quantity > 0))
        {
            var line = po.Lines.FirstOrDefault(x => x.Id == l.LineId) ?? throw AppException.NotFound("po_line_not_found");
            var product = await qty.ProductAsync(line.ProductId, ct);
            var baseQty = l.Quantity * line.Factor;
            var limit = line.OrderedBase * (1 + OverReceiptTolerance);
            if (line.ReceivedBase + baseQty > limit)
                throw new AppException(409, "over_receipt", ("sku", product.Sku),
                    ("remaining", (line.RemainingBase / line.Factor).ToString("0.####")), ("unit", line.Unit));

            Guid? batchId = null;
            if (product.TrackBatches)
            {
                if (string.IsNullOrWhiteSpace(l.BatchNumber)) throw new AppException(422, "batch_required", ("sku", product.Sku));
                batchId = (await ledger.BatchAsync(line.ProductId, l.BatchNumber, l.ExpiresOn, ct)).Id;
            }
            line.ReceivedBase += baseQty;
            changes.Add(new StockChange(line.ProductId, po.WarehouseId, baseQty, batchId, line.BaseUnitCost));
            receipt.Lines.Add(new GoodsReceiptLine
            {
                GoodsReceiptId = receipt.Id, PurchaseOrderLineId = line.Id, ProductId = line.ProductId, BatchId = batchId,
                QuantityBase = baseQty, UnitCost = line.BaseUnitCost,
            });
        }
        if (changes.Count == 0) throw new AppException(422, "receipt_empty");

        var op = await ledger.PostAsync(new PostRequest(OperationKind.Receipt, "GRN", changes, input.IdempotencyKey,
            Note: receipt.Note, SourceType: nameof(GoodsReceipt), SourceId: receipt.Id, Number: receipt.Number), ct);
        receipt.StockOperationId = op.Id;
        po.Received(DateTimeOffset.UtcNow);
        db.GoodsReceipts.Add(receipt);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await ledger.PublishAsync(ct);
        return receipt;
    }

    public async Task<PurchaseOrder> Load(Guid id, CancellationToken ct)
    {
        var po = await db.PurchaseOrders.Include(p => p.Lines.OrderBy(l => l.Position)).Include(p => p.Approvals)
                     .FirstOrDefaultAsync(p => p.Id == id, ct)
                 ?? throw AppException.NotFound("po_not_found");
        // Warehouse staff only deal with orders for their warehouses.
        var allowed = await access.AllowedAsync(ct);
        if (allowed is not null && !allowed.Contains(po.WarehouseId)) throw AppException.NotFound("po_not_found");
        return po;
    }

    public Task<List<ApprovalRule>> Rules(CancellationToken ct) => db.ApprovalRules.AsNoTracking().OrderBy(r => r.Level).ToListAsync(ct);
}
