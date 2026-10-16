using Tallyard.Api.Common;

namespace Tallyard.Api.Modules.Sales;

public enum SoStatus { Draft, OnHold, Confirmed, PartiallyShipped, Shipped, Closed, Cancelled }

/// <summary>
/// A customer's order. Confirming it reserves stock (so nobody else can sell it) unless the
/// customer is over their credit, in which case it waits on hold for finance. Every status
/// change is a method here that refuses transitions which aren't allowed.
/// </summary>
public class SalesOrder : TenantEntity, IAudited
{
    public required string Number { get; set; }
    public Guid CustomerId { get; set; }
    /// <summary>Ships from this warehouse.</summary>
    public Guid WarehouseId { get; set; }
    public SoStatus Status { get; set; } = SoStatus.Draft;
    public DateOnly OrderDate { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public DateOnly? RequestedOn { get; set; }
    /// <summary>The customer's own reference (their PO number).</summary>
    public string? CustomerReference { get; set; }
    public string? Note { get; set; }
    public decimal Subtotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    /// <summary>Why the order is on hold (credit limit, overdue invoices), for the people releasing it.</summary>
    public string? HoldReason { get; set; }
    public Guid? ReleasedBy { get; set; }
    public string? ReleaseNote { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public List<SalesOrderLine> Lines { get; set; } = [];
    public uint Version { get; set; }

    public bool Editable => Status == SoStatus.Draft;
    public bool Shippable => Status is SoStatus.Confirmed or SoStatus.PartiallyShipped;

    public void Recalculate()
    {
        foreach (var l in Lines) l.Recalculate();
        Subtotal = Lines.Sum(l => l.Net);
        TaxTotal = Lines.Sum(l => l.Tax);
        Total = Subtotal + TaxTotal;
    }

    public void Hold(string reason)
    {
        Require(Status == SoStatus.Draft, "so_not_draft");
        Status = SoStatus.OnHold;
        HoldReason = reason;
    }

    public void Confirm(DateTimeOffset now)
    {
        Require(Status is SoStatus.Draft or SoStatus.OnHold, "so_not_draft");
        if (Lines.Count == 0) throw new AppException(422, "so_no_lines");
        Status = SoStatus.Confirmed;
        ConfirmedAt = now;
    }

    public void Release(Guid userId, string note, DateTimeOffset now)
    {
        Require(Status == SoStatus.OnHold, "so_not_on_hold");
        ReleasedBy = userId;
        ReleaseNote = note;
        ReleasedAt = now;
        Confirm(now);
    }

    public void AfterShipment(DateTimeOffset now)
    {
        Status = Lines.All(l => l.ShippedBase >= l.OrderedBase) ? SoStatus.Shipped : SoStatus.PartiallyShipped;
        if (Status == SoStatus.Shipped) ClosedAt = now;
    }

    /// <summary>Stops shipping what's left. The caller releases the remaining reservations.</summary>
    public void Close(DateTimeOffset now)
    {
        Require(Status is SoStatus.PartiallyShipped, "so_not_partial");
        Status = SoStatus.Closed;
        ClosedAt = now;
    }

    public void Cancel(DateTimeOffset now)
    {
        Require(Status is SoStatus.Draft or SoStatus.OnHold or SoStatus.Confirmed, "so_not_cancellable");
        Status = SoStatus.Cancelled;
        ClosedAt = now;
    }

    static void Require(bool ok, string code)
    {
        if (!ok) throw AppException.Conflict(code);
    }
}

public class SalesOrderLine : Entity, ITenantOwned, IAuditedChild
{
    public Guid TenantId { get; set; }
    public Guid SalesOrderId { get; set; }
    public Guid ProductId { get; set; }
    public required string Unit { get; set; }
    public decimal Factor { get; set; } = 1;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal TaxRate { get; set; }
    public decimal Net { get; set; }
    public decimal Tax { get; set; }
    /// <summary>Base units promised to this line and not yet shipped.</summary>
    public decimal ReservedBase { get; set; }
    public decimal ShippedBase { get; set; }
    public decimal InvoicedBase { get; set; }
    public int Position { get; set; }

    public decimal OrderedBase => Quantity * Factor;
    /// <summary>Ordered, not shipped, and not reserved: waiting for stock.</summary>
    public decimal BackorderedBase => Math.Max(0, OrderedBase - ShippedBase - ReservedBase);
    /// <summary>Net price of one base unit, after discount.</summary>
    public decimal BaseUnitNet => Factor == 0 ? 0 : UnitPrice * (1 - DiscountPercent / 100) / Factor;

    public void Recalculate()
    {
        Net = Math.Round(Quantity * UnitPrice * (1 - DiscountPercent / 100), 2);
        Tax = Math.Round(Net * TaxRate / 100, 2);
    }

    Guid IAuditedChild.AuditParentId => SalesOrderId;
    string IAuditedChild.AuditParentType => nameof(SalesOrder);
    string IAuditedChild.AuditField => $"line {Position + 1}";
    object? IAuditedChild.AuditValue => $"{Quantity:0.####} {Unit} @ {UnitPrice:0.####} -{DiscountPercent:0.##}%";
}

/// <summary>A delivery: goods that left the warehouse for a customer (the delivery note).</summary>
public class Shipment : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public required string Number { get; set; }
    public Guid SalesOrderId { get; set; }
    public Guid CustomerId { get; set; }
    public Guid WarehouseId { get; set; }
    public Guid StockOperationId { get; set; }
    public Guid? ShippedBy { get; set; }
    public DateTimeOffset ShippedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Carrier { get; set; }
    public string? TrackingNumber { get; set; }
    public Guid? InvoiceId { get; set; }
    public List<ShipmentLine> Lines { get; set; } = [];
}

public class ShipmentLine : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid ShipmentId { get; set; }
    public Guid SalesOrderLineId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? BatchId { get; set; }
    public decimal QuantityBase { get; set; }
}

public enum InvoiceStatus { Open, PartiallyPaid, Paid, Void }

/// <summary>An invoice for one shipment: the customer pays for what was delivered.</summary>
public class Invoice : Entity, ITenantOwned, IAudited
{
    public Guid TenantId { get; set; }
    public required string Number { get; set; }
    public Guid CustomerId { get; set; }
    public Guid SalesOrderId { get; set; }
    public Guid ShipmentId { get; set; }
    public DateOnly IssueDate { get; set; }
    public DateOnly DueDate { get; set; }
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Open;
    public decimal Subtotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
    public decimal AmountPaid { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<InvoiceLine> Lines { get; set; } = [];
    public uint Version { get; set; }

    public decimal Balance => Total - AmountPaid;

    public void Pay(decimal amount)
    {
        if (amount <= 0 || amount > Balance) throw new AppException(422, "payment_exceeds_balance", ("number", Number));
        AmountPaid += amount;
        Status = AmountPaid >= Total ? InvoiceStatus.Paid : InvoiceStatus.PartiallyPaid;
    }
}

public class InvoiceLine : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid InvoiceId { get; set; }
    public Guid ProductId { get; set; }
    public string Description { get; set; } = "";
    public decimal Quantity { get; set; }
    public required string Unit { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal TaxRate { get; set; }
    public decimal Net { get; set; }
    public decimal Tax { get; set; }
}

public enum PaymentMethod { BankTransfer, Cash, Card, Check }

/// <summary>Money received from a customer, allocated to one or more of their open invoices.</summary>
public class Payment : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public required string Number { get; set; }
    public Guid CustomerId { get; set; }
    public DateOnly ReceivedOn { get; set; }
    public decimal Amount { get; set; }
    public PaymentMethod Method { get; set; }
    public string? Reference { get; set; }
    public Guid? RecordedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<PaymentAllocation> Allocations { get; set; } = [];
}

public class PaymentAllocation : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid PaymentId { get; set; }
    public Guid InvoiceId { get; set; }
    public decimal Amount { get; set; }
}
