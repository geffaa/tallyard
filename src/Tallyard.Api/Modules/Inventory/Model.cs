using Tallyard.Api.Common;

namespace Tallyard.Api.Modules.Inventory;

/// <summary>A lot of a batch-tracked product, with its expiry date.</summary>
public class Batch : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid ProductId { get; set; }
    public required string Number { get; set; }
    public DateOnly? ExpiresOn { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Stock of one product in one warehouse: a projection of the movements, updated in the same
/// transaction as each movement, under a row lock. Only <see cref="StockLedger"/> writes it.
/// The database enforces <c>on_hand &gt;= 0</c> and <c>0 &lt;= reserved &lt;= on_hand</c>.
/// </summary>
public class StockBalance : ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid ProductId { get; set; }
    public Guid WarehouseId { get; set; }
    public decimal OnHand { get; set; }
    /// <summary>Promised to confirmed sales orders, not yet shipped.</summary>
    public decimal Reserved { get; set; }
    /// <summary>Moving average cost per base unit.</summary>
    public decimal AvgCost { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public decimal Available => OnHand - Reserved;
}

/// <summary>On-hand per batch, for batch-tracked products; sums to the product's balance.</summary>
public class BatchBalance : ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid ProductId { get; set; }
    public Guid WarehouseId { get; set; }
    public Guid BatchId { get; set; }
    public decimal OnHand { get; set; }
}

public enum OperationKind { Adjustment, TransferOut, TransferIn, Receipt, Shipment, Return }

/// <summary>
/// One posting to the ledger: a header for the movements it produced. Its idempotency key makes
/// a retried request (double click, network retry) return the first result instead of posting twice.
/// </summary>
public class StockOperation : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public OperationKind Kind { get; set; }
    /// <summary>Document number shown to people, e.g. ADJ-2026-000042.</summary>
    public required string Number { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? Reason { get; set; }
    public string? Note { get; set; }
    /// <summary>The document that caused it (a transfer, a goods receipt, a shipment).</summary>
    public string? SourceType { get; set; }
    public Guid? SourceId { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<StockMovement> Movements { get; set; } = [];
}

/// <summary>
/// One change to stock, in base units (positive in, negative out). Append-only: a trigger rejects
/// UPDATE and DELETE; a mistake is corrected by posting the opposite movement.
/// </summary>
public class StockMovement : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid OperationId { get; set; }
    public Guid ProductId { get; set; }
    public Guid WarehouseId { get; set; }
    public Guid? BatchId { get; set; }
    public decimal Quantity { get; set; }
    /// <summary>Cost per base unit: the purchase cost for receipts, the average cost for issues.</summary>
    public decimal UnitCost { get; set; }
    /// <summary>On-hand of the product in this warehouse right after this movement (for the stock card).</summary>
    public decimal BalanceAfter { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum TransferStatus { InTransit, Received, Cancelled }

/// <summary>Stock moving between warehouses. Shipping takes it out of the source; it is "in
/// transit" (in neither warehouse) until the destination receives it.</summary>
public class Transfer : TenantEntity, IAudited
{
    public required string Number { get; set; }
    public Guid FromWarehouseId { get; set; }
    public Guid ToWarehouseId { get; set; }
    public TransferStatus Status { get; set; } = TransferStatus.InTransit;
    public string? Note { get; set; }
    public Guid? ShippedBy { get; set; }
    public DateTimeOffset ShippedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? ReceivedBy { get; set; }
    public DateTimeOffset? ReceivedAt { get; set; }
    public List<TransferLine> Lines { get; set; } = [];
    public uint Version { get; set; }
}

public class TransferLine : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid TransferId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? BatchId { get; set; }
    /// <summary>Base units.</summary>
    public decimal Quantity { get; set; }
    /// <summary>The cost it left the source at; the destination receives it at this cost.</summary>
    public decimal UnitCost { get; set; }
}

public enum CountStatus { Counting, Posted, Cancelled }

/// <summary>
/// A stock count (stock opname). Starting it records what the system expects per product (and
/// batch); people then count what is on the shelf. Posting adjusts by counted − expected, so
/// sales and receipts that happen while counting are not undone by the count.
/// </summary>
public class StockCount : TenantEntity, IAudited
{
    public required string Number { get; set; }
    public Guid WarehouseId { get; set; }
    public Guid? CategoryId { get; set; }
    public CountStatus Status { get; set; } = CountStatus.Counting;
    public string? Note { get; set; }
    public Guid? StartedBy { get; set; }
    public Guid? PostedBy { get; set; }
    public DateTimeOffset? PostedAt { get; set; }
    public Guid? AdjustmentId { get; set; }
    public List<StockCountLine> Lines { get; set; } = [];
    public uint Version { get; set; }
}

public class StockCountLine : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid StockCountId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? BatchId { get; set; }
    /// <summary>On hand when the count started (base units).</summary>
    public decimal Expected { get; set; }
    /// <summary>Null until someone counts it.</summary>
    public decimal? Counted { get; set; }
    public Guid? CountedBy { get; set; }
    public DateTimeOffset? CountedAt { get; set; }
}
