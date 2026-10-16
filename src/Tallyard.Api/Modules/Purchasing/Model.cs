using Tallyard.Api.Common;
using Tallyard.Api.Modules.Identity;

namespace Tallyard.Api.Modules.Purchasing;

public enum PoStatus { Draft, PendingApproval, Approved, Sent, PartiallyReceived, Received, Closed, Cancelled }

/// <summary>
/// A purchase order and its rules. Every status change goes through a method here, so a
/// transition that isn't allowed (approving a draft, editing an approved order, receiving a
/// cancelled one) is refused by the domain, whatever the UI shows.
/// </summary>
public class PurchaseOrder : TenantEntity, IAudited
{
    public required string Number { get; set; }
    public Guid SupplierId { get; set; }
    /// <summary>Where the goods are delivered.</summary>
    public Guid WarehouseId { get; set; }
    public PoStatus Status { get; set; } = PoStatus.Draft;
    public DateOnly OrderDate { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public DateOnly? ExpectedOn { get; set; }
    public string? Note { get; set; }
    public decimal Subtotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public List<PurchaseOrderLine> Lines { get; set; } = [];
    public List<PoApproval> Approvals { get; set; } = [];
    public uint Version { get; set; }

    public bool Editable => Status == PoStatus.Draft;
    public bool Receivable => Status is PoStatus.Approved or PoStatus.Sent or PoStatus.PartiallyReceived;

    public void Recalculate()
    {
        foreach (var l in Lines) l.Recalculate();
        Subtotal = Lines.Sum(l => l.Net);
        TaxTotal = Lines.Sum(l => l.Tax);
        Total = Subtotal + TaxTotal;
    }

    /// <summary>The approval levels this order needs, given its total: every rule whose threshold it reaches.</summary>
    public static List<ApprovalRule> Required(decimal total, IEnumerable<ApprovalRule> rules) =>
        rules.Where(r => total >= r.MinTotal).OrderBy(r => r.Level).ToList();

    public void Submit(IReadOnlyList<ApprovalRule> rules, DateTimeOffset now)
    {
        Require(Status == PoStatus.Draft, "po_not_draft");
        if (Lines.Count == 0) throw new AppException(422, "po_no_lines");
        Recalculate();
        SubmittedAt = now;
        Approvals.Clear();
        if (Required(Total, rules).Count == 0) Approve(now);
        else Status = PoStatus.PendingApproval;
    }

    /// <summary>
    /// Records one approval. Levels are approved in order; each needs the rule's role, and nobody
    /// may approve an order they created (segregation of duties).
    /// </summary>
    public void ApproveLevel(IReadOnlyList<ApprovalRule> rules, Guid userId, Role role, string? comment, DateTimeOffset now)
    {
        Require(Status == PoStatus.PendingApproval, "po_not_pending");
        if (userId == CreatedBy) throw new AppException(403, "po_self_approval");
        var required = Required(Total, rules);
        var next = required.FirstOrDefault(r => Approvals.All(a => a.Level != r.Level))
                   ?? throw new InvalidOperationException("No approval level left");
        if (role != next.Role && role != Role.Owner) throw new AppException(403, "po_wrong_approver", ("role", next.Role.ToString()));
        if (Approvals.Any(a => a.ApprovedBy == userId)) throw new AppException(403, "po_already_approved_by_you");
        Approvals.Add(new PoApproval
        {
            PurchaseOrderId = Id, Level = next.Level, Role = next.Role, ApprovedBy = userId, Comment = comment, At = now,
        });
        if (required.All(r => Approvals.Any(a => a.Level == r.Level))) Approve(now);
    }

    /// <summary>A rejection sends the order back to draft, with the reason kept in the history.</summary>
    public void Reject(Guid userId, string comment, DateTimeOffset now)
    {
        Require(Status == PoStatus.PendingApproval, "po_not_pending");
        if (userId == CreatedBy) throw new AppException(403, "po_self_approval");
        Status = PoStatus.Draft;
        Approvals.Clear();
        RejectedBy = userId;
        RejectionReason = comment;
        RejectedAt = now;
    }

    public Guid? RejectedBy { get; set; }
    public string? RejectionReason { get; set; }
    public DateTimeOffset? RejectedAt { get; set; }

    void Approve(DateTimeOffset now)
    {
        Status = PoStatus.Approved;
        ApprovedAt = now;
    }

    public void MarkSent(DateTimeOffset now)
    {
        Require(Status is PoStatus.Approved or PoStatus.Sent, "po_not_approved");
        Status = PoStatus.Sent;
        SentAt ??= now;
    }

    /// <summary>Updates received quantities after a goods receipt and moves the status on.</summary>
    public void Received(DateTimeOffset now)
    {
        Status = Lines.All(l => l.ReceivedBase >= l.OrderedBase) ? PoStatus.Received : PoStatus.PartiallyReceived;
        if (Status == PoStatus.Received) ClosedAt = now;
    }

    /// <summary>Closes an order that won't be delivered in full: the rest is no longer expected.</summary>
    public void Close(DateTimeOffset now)
    {
        Require(Status == PoStatus.PartiallyReceived, "po_not_partial");
        Status = PoStatus.Closed;
        ClosedAt = now;
    }

    public void Cancel(DateTimeOffset now)
    {
        Require(Status is PoStatus.Draft or PoStatus.PendingApproval or PoStatus.Approved or PoStatus.Sent, "po_not_cancellable");
        Status = PoStatus.Cancelled;
        ClosedAt = now;
    }

    static void Require(bool ok, string code)
    {
        if (!ok) throw AppException.Conflict(code);
    }
}

public class PurchaseOrderLine : Entity, ITenantOwned, IAuditedChild
{
    public Guid TenantId { get; set; }
    public Guid PurchaseOrderId { get; set; }
    public Guid ProductId { get; set; }
    /// <summary>The unit ordered in, and how many base units it holds (a snapshot: later
    /// changes to the product don't change what was ordered).</summary>
    public required string Unit { get; set; }
    public decimal Factor { get; set; } = 1;
    public decimal Quantity { get; set; }
    /// <summary>Price per <see cref="Unit"/>, before tax.</summary>
    public decimal UnitPrice { get; set; }
    public decimal TaxRate { get; set; }
    public decimal Net { get; set; }
    public decimal Tax { get; set; }
    /// <summary>Base units received so far.</summary>
    public decimal ReceivedBase { get; set; }
    public int Position { get; set; }

    public decimal OrderedBase => Quantity * Factor;
    public decimal RemainingBase => Math.Max(0, OrderedBase - ReceivedBase);
    /// <summary>Cost of one base unit, which is what stock is valued at.</summary>
    public decimal BaseUnitCost => Factor == 0 ? 0 : UnitPrice / Factor;

    public void Recalculate()
    {
        Net = Math.Round(Quantity * UnitPrice, 2);
        Tax = Math.Round(Net * TaxRate / 100, 2);
    }

    Guid IAuditedChild.AuditParentId => PurchaseOrderId;
    string IAuditedChild.AuditParentType => nameof(PurchaseOrder);
    string IAuditedChild.AuditField => $"line {Position + 1}";
    object? IAuditedChild.AuditValue => $"{Quantity:0.####} {Unit} @ {UnitPrice:0.####}";
}

/// <summary>Orders at or above <see cref="MinTotal"/> need an approval at this level, by this role.</summary>
public class ApprovalRule : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public int Level { get; set; }
    public decimal MinTotal { get; set; }
    public Role Role { get; set; }
}

public class PoApproval : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid PurchaseOrderId { get; set; }
    public int Level { get; set; }
    public Role Role { get; set; }
    public Guid ApprovedBy { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>Goods that arrived against a purchase order. It posts a Receipt to the stock ledger.</summary>
public class GoodsReceipt : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public required string Number { get; set; }
    public Guid PurchaseOrderId { get; set; }
    public Guid WarehouseId { get; set; }
    public Guid StockOperationId { get; set; }
    public Guid? ReceivedBy { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Note { get; set; }
    public List<GoodsReceiptLine> Lines { get; set; } = [];
}

public class GoodsReceiptLine : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid GoodsReceiptId { get; set; }
    public Guid PurchaseOrderLineId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? BatchId { get; set; }
    public decimal QuantityBase { get; set; }
    public decimal UnitCost { get; set; }
}

/// <summary>When available + incoming stock of a product in a warehouse falls to the reorder
/// point, suggest ordering <see cref="ReorderQuantity"/> base units from <see cref="SupplierId"/>.</summary>
public class ReorderRule : Entity, ITenantOwned, IAudited
{
    public Guid TenantId { get; set; }
    public Guid ProductId { get; set; }
    public Guid WarehouseId { get; set; }
    public Guid SupplierId { get; set; }
    public decimal ReorderPoint { get; set; }
    public decimal ReorderQuantity { get; set; }
}
