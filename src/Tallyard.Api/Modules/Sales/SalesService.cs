using Microsoft.EntityFrameworkCore;
using Tallyard.Api.Common;
using Tallyard.Api.Infrastructure;
using Tallyard.Api.Modules.Inventory;

namespace Tallyard.Api.Modules.Sales;

public record SoLineInput(Guid ProductId, decimal Quantity, string? Unit, decimal? UnitPrice, decimal? DiscountPercent, decimal? TaxRate);
public record SoInput(Guid CustomerId, Guid WarehouseId, DateOnly? RequestedOn, string? CustomerReference, string? Note, SoLineInput[] Lines, uint? Version);
public record ShipLineInput(Guid LineId, decimal Quantity);
public record ShipInput(ShipLineInput[] Lines, string? Carrier, string? TrackingNumber, string? IdempotencyKey);
public record PaymentInput(Guid CustomerId, decimal Amount, DateOnly? ReceivedOn, string Method, string? Reference, PaymentAllocationInput[]? Allocations);
public record PaymentAllocationInput(Guid InvoiceId, decimal Amount);

/// <summary>What a customer owes now, and how much more they'd owe if this order went through.</summary>
public record CreditCheck(decimal Limit, decimal OpenInvoices, decimal OpenOrders, decimal ThisOrder, int OverdueInvoices)
{
    public decimal Exposure => OpenInvoices + OpenOrders + ThisOrder;
    public bool OverLimit => Exposure > Limit;
}

/// <summary>
/// Order-to-cash use cases: orders reserve stock on confirmation, shipments consume the
/// reservation, invoices bill what was shipped, payments settle invoices. Each step runs in one
/// transaction and locks the rows it changes, so concurrent clicks can't double-ship, double-bill
/// or over-allocate.
/// </summary>
public sealed class SalesService(
    AppDbContext db, CurrentUser me, DocumentNumbers numbers, StockLedger ledger, Quantities qty, WarehouseAccess access)
{
    /// <summary>An invoice this many days past due puts new orders on hold, whatever the limit.</summary>
    public const int OverdueHoldDays = 30;

    public async Task<SalesOrder> CreateAsync(SoInput input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var so = new SalesOrder { Number = await numbers.NextAsync("SO", ct), CreatedBy = me.UserId };
        await Apply(so, input, ct);
        db.SalesOrders.Add(so);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return so;
    }

    public async Task<SalesOrder> UpdateAsync(Guid id, SoInput input, CancellationToken ct)
    {
        var so = await Load(id, ct);
        if (!so.Editable) throw AppException.Conflict("so_not_draft");
        db.Entry(so).Property(x => x.Version).OriginalValue = input.Version ?? so.Version;
        await Apply(so, input, ct);
        await db.SaveChangesAsync(ct);
        return so;
    }

    async Task Apply(SalesOrder so, SoInput input, CancellationToken ct)
    {
        if (!await db.Customers.AnyAsync(c => c.Id == input.CustomerId && c.IsActive, ct)) throw AppException.NotFound("customer_not_found");
        await access.EnsureAsync(input.WarehouseId, ct);
        so.CustomerId = input.CustomerId;
        so.WarehouseId = input.WarehouseId;
        so.RequestedOn = input.RequestedOn;
        so.CustomerReference = Blank(input.CustomerReference);
        so.Note = Blank(input.Note);
        db.SalesOrderLines.RemoveRange(so.Lines);
        so.Lines.Clear();
        var position = 0;
        foreach (var l in input.Lines)
        {
            var product = await qty.ProductAsync(l.ProductId, ct);
            var unit = string.IsNullOrWhiteSpace(l.Unit) ? product.BaseUnit : l.Unit.Trim().ToLowerInvariant();
            var factor = await qty.ToBaseAsync(l.ProductId, 1, unit, ct);
            so.Lines.Add(new SalesOrderLine
            {
                SalesOrderId = so.Id, ProductId = l.ProductId, Unit = unit, Factor = factor, Quantity = l.Quantity,
                UnitPrice = l.UnitPrice ?? Math.Round(product.SalePrice * factor, 4),
                DiscountPercent = l.DiscountPercent ?? 0, TaxRate = l.TaxRate ?? product.TaxRate, Position = position++,
            });
        }
        so.Recalculate();
    }

    /// <summary>
    /// Confirms an order: checks credit, then reserves stock. A customer over their limit (or
    /// with a long-overdue invoice) puts the order on hold for finance instead. Without
    /// <paramref name="allowBackorder"/>, any line short of stock refuses the whole confirmation.
    /// </summary>
    public async Task<SalesOrder> ConfirmAsync(Guid id, bool allowBackorder, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var so = await Lock(id, ct);
        if (so.Status != SoStatus.Draft) throw AppException.Conflict("so_not_draft");
        if (so.Lines.Count == 0) throw new AppException(422, "so_no_lines");

        var credit = await CheckCreditAsync(so.CustomerId, so.Total, so.Id, ct);
        if (credit.OverdueInvoices > 0 || credit.OverLimit)
        {
            so.Hold(credit.OverdueInvoices > 0 ? "overdue" : "credit_limit");
        }
        else
        {
            await Reserve(so, allOrNothing: !allowBackorder, ct);
            so.Confirm(DateTimeOffset.UtcNow);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await ledger.PublishAsync(ct);
        return so;
    }

    /// <summary>Finance lets a held order through (recorded with a note) and stock is reserved.</summary>
    public async Task<SalesOrder> ReleaseAsync(Guid id, string note, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var so = await Lock(id, ct);
        so.Release(me.RequiredUserId, note, DateTimeOffset.UtcNow);
        await Reserve(so, allOrNothing: false, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await ledger.PublishAsync(ct);
        return so;
    }

    /// <summary>Tries again to reserve stock for back-ordered lines (after a delivery came in).</summary>
    public async Task<SalesOrder> ReserveAgainAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var so = await Lock(id, ct);
        if (!so.Shippable) throw AppException.Conflict("so_not_confirmed");
        await Reserve(so, allOrNothing: false, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await ledger.PublishAsync(ct);
        return so;
    }

    async Task Reserve(SalesOrder so, bool allOrNothing, CancellationToken ct)
    {
        var wanted = so.Lines.Where(l => l.BackorderedBase > 0)
            .Select(l => (l.ProductId, so.WarehouseId, l.BackorderedBase)).ToList();
        if (wanted.Count == 0) return;
        var got = await ledger.ReserveAsync(wanted, allOrNothing, ct);
        // Hand out what was reserved per product to its lines, in line order.
        foreach (var l in so.Lines.OrderBy(l => l.Position))
        {
            var key = (l.ProductId, so.WarehouseId);
            if (!got.TryGetValue(key, out var left) || left <= 0) continue;
            var take = Math.Min(left, l.BackorderedBase);
            l.ReservedBase += take;
            got[key] = left - take;
        }
    }

    public async Task<SalesOrder> CancelAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var so = await Lock(id, ct);
        so.Cancel(DateTimeOffset.UtcNow);
        await ReleaseAll(so, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await ledger.PublishAsync(ct);
        return so;
    }

    public async Task<SalesOrder> CloseAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var so = await Lock(id, ct);
        so.Close(DateTimeOffset.UtcNow);
        await ReleaseAll(so, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await ledger.PublishAsync(ct);
        return so;
    }

    async Task ReleaseAll(SalesOrder so, CancellationToken ct)
    {
        var held = so.Lines.Where(l => l.ReservedBase > 0).Select(l => (l.ProductId, so.WarehouseId, l.ReservedBase)).ToList();
        if (held.Count > 0) await ledger.ReleaseAsync(held, ct);
        foreach (var l in so.Lines) l.ReservedBase = 0;
    }

    /// <summary>
    /// Ships reserved stock. Only what is reserved for a line can ship (so a shipment never takes
    /// stock promised to someone else); batches are picked earliest-expiry first by the ledger.
    /// </summary>
    public async Task<Shipment> ShipAsync(Guid id, ShipInput input, CancellationToken ct, DateTimeOffset? at = null)
    {
        if (input.IdempotencyKey is { } key &&
            await db.StockOperations.AsNoTracking().FirstOrDefaultAsync(o => o.IdempotencyKey == key, ct) is { SourceId: { } done })
            return await db.Shipments.AsNoTracking().Include(s => s.Lines).FirstAsync(s => s.Id == done, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var so = await Lock(id, ct);
        await access.EnsureAsync(so.WarehouseId, ct);
        if (!so.Shippable) throw AppException.Conflict("so_not_shippable");

        var shipment = new Shipment
        {
            Number = await numbers.NextAsync("DO", ct), SalesOrderId = so.Id, CustomerId = so.CustomerId, WarehouseId = so.WarehouseId,
            ShippedBy = me.UserId, Carrier = Blank(input.Carrier), TrackingNumber = Blank(input.TrackingNumber),
            ShippedAt = at ?? DateTimeOffset.UtcNow,
        };
        var picks = new List<(SalesOrderLine Line, decimal Base)>();
        foreach (var l in input.Lines.Where(l => l.Quantity > 0))
        {
            var line = so.Lines.FirstOrDefault(x => x.Id == l.LineId) ?? throw AppException.NotFound("so_line_not_found");
            var baseQty = l.Quantity * line.Factor;
            if (baseQty > line.ReservedBase)
            {
                var product = await qty.ProductAsync(line.ProductId, ct);
                throw new AppException(409, "ship_more_than_reserved", ("sku", product.Sku),
                    ("reserved", (line.ReservedBase / line.Factor).ToString("0.####")), ("unit", line.Unit));
            }
            picks.Add((line, baseQty));
        }
        if (picks.Count == 0) throw new AppException(422, "shipment_empty");

        // One change per order line, so the resulting movements map back to their lines in order.
        var changes = picks.Select(p => new StockChange(p.Line.ProductId, so.WarehouseId, -p.Base, FromReserved: true)).ToList();
        var op = await ledger.PostAsync(new PostRequest(OperationKind.Shipment, "DO", changes, input.IdempotencyKey,
            SourceType: nameof(Shipment), SourceId: shipment.Id, Number: shipment.Number, At: at), ct);

        var movements = new Queue<StockMovement>(op.Movements);
        foreach (var (line, baseQty) in picks)
        {
            var left = baseQty;
            while (left > 0 && movements.Count > 0)
            {
                var m = movements.Dequeue();
                shipment.Lines.Add(new ShipmentLine
                {
                    ShipmentId = shipment.Id, SalesOrderLineId = line.Id, ProductId = line.ProductId, BatchId = m.BatchId, QuantityBase = -m.Quantity,
                });
                left += m.Quantity;
            }
            line.ReservedBase -= baseQty;
            line.ShippedBase += baseQty;
        }
        shipment.StockOperationId = op.Id;
        so.AfterShipment(DateTimeOffset.UtcNow);
        db.Shipments.Add(shipment);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await ledger.PublishAsync(ct);
        return shipment;
    }

    /// <summary>Bills a shipment. Each shipment is invoiced once (its row is locked while checking).</summary>
    public async Task<Invoice> InvoiceAsync(Guid shipmentId, DateOnly? issueDate, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var shipment = await db.Shipments.FromSql($"SELECT * FROM shipments WHERE id = {shipmentId} FOR UPDATE")
                           .AsAsyncEnumerable().FirstOrDefaultAsync(ct)
                       ?? throw AppException.NotFound("shipment_not_found");
        if (shipment.InvoiceId is not null) throw AppException.Conflict("shipment_already_invoiced");
        await db.Entry(shipment).Collection(s => s.Lines).LoadAsync(ct);
        var so = await db.SalesOrders.Include(s => s.Lines).FirstAsync(s => s.Id == shipment.SalesOrderId, ct);
        var customer = await db.Customers.AsNoTracking().FirstAsync(c => c.Id == shipment.CustomerId, ct);
        var issued = issueDate ?? DateOnly.FromDateTime(DateTime.UtcNow);

        var invoice = new Invoice
        {
            Number = await numbers.NextAsync("INV", ct), CustomerId = shipment.CustomerId, SalesOrderId = so.Id, ShipmentId = shipment.Id,
            IssueDate = issued, DueDate = issued.AddDays(customer.PaymentTermsDays), CreatedBy = me.UserId,
        };
        foreach (var g in shipment.Lines.GroupBy(l => l.SalesOrderLineId))
        {
            var line = so.Lines.First(l => l.Id == g.Key);
            var product = await qty.ProductAsync(line.ProductId, ct);
            var baseQty = g.Sum(l => l.QuantityBase);
            var quantity = baseQty / line.Factor;
            var net = Math.Round(quantity * line.UnitPrice * (1 - line.DiscountPercent / 100), 2);
            invoice.Lines.Add(new InvoiceLine
            {
                InvoiceId = invoice.Id, ProductId = line.ProductId, Description = $"{product.Sku} {product.Name}", Quantity = quantity,
                Unit = line.Unit, UnitPrice = line.UnitPrice, DiscountPercent = line.DiscountPercent, TaxRate = line.TaxRate,
                Net = net, Tax = Math.Round(net * line.TaxRate / 100, 2),
            });
            line.InvoicedBase += baseQty;
        }
        invoice.Subtotal = invoice.Lines.Sum(l => l.Net);
        invoice.TaxTotal = invoice.Lines.Sum(l => l.Tax);
        invoice.Total = invoice.Subtotal + invoice.TaxTotal;
        shipment.InvoiceId = invoice.Id;
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return invoice;
    }

    /// <summary>
    /// Records a payment and allocates it: to the invoices given, or else to the customer's open
    /// invoices oldest due first. The invoices are locked, so two payments can't both settle the
    /// same balance; paying more than is owed is refused.
    /// </summary>
    public async Task<Payment> PayAsync(PaymentInput input, CancellationToken ct)
    {
        if (!Enum.TryParse<PaymentMethod>(input.Method, out var method)) throw new AppException(422, "validation_failed");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var open = await db.Invoices.FromSql($"""
                SELECT *, xmin FROM invoices
                WHERE customer_id = {input.CustomerId} AND status IN ('Open', 'PartiallyPaid')
                ORDER BY due_date, number FOR UPDATE
                """).AsAsyncEnumerable().ToListAsync(ct);
        var payment = new Payment
        {
            Number = await numbers.NextAsync("PAY", ct), CustomerId = input.CustomerId, Amount = input.Amount, Method = method,
            ReceivedOn = input.ReceivedOn ?? DateOnly.FromDateTime(DateTime.UtcNow), Reference = Blank(input.Reference), RecordedBy = me.UserId,
        };

        var plan = input.Allocations is { Length: > 0 } given
            ? given.Select(a => (open.FirstOrDefault(i => i.Id == a.InvoiceId) ?? throw AppException.NotFound("invoice_not_open"), a.Amount)).ToList()
            : AutoAllocate(open, input.Amount);
        if (plan.Sum(p => p.Item2) != input.Amount)
            throw new AppException(422, "payment_unallocated", ("open", open.Sum(i => i.Balance).ToString("0.00")));
        foreach (var (invoice, amount) in plan)
        {
            invoice.Pay(amount);
            payment.Allocations.Add(new PaymentAllocation { PaymentId = payment.Id, InvoiceId = invoice.Id, Amount = amount });
        }
        db.Payments.Add(payment);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return payment;
    }

    static List<(Invoice, decimal)> AutoAllocate(List<Invoice> open, decimal amount)
    {
        var plan = new List<(Invoice, decimal)>();
        foreach (var i in open)
        {
            if (amount <= 0) break;
            var take = Math.Min(i.Balance, amount);
            plan.Add((i, take));
            amount -= take;
        }
        return plan;
    }

    /// <summary>
    /// Credit exposure: open invoice balances, plus orders already promised but not yet invoiced
    /// (unshipped quantities of open orders and shipped-but-unbilled quantities), plus this order.
    /// </summary>
    public async Task<CreditCheck> CheckCreditAsync(Guid customerId, decimal thisOrder, Guid? exclude, CancellationToken ct)
    {
        var customer = await db.Customers.AsNoTracking().FirstAsync(c => c.Id == customerId, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var invoices = await db.Invoices.AsNoTracking()
            .Where(i => i.CustomerId == customerId && (i.Status == InvoiceStatus.Open || i.Status == InvoiceStatus.PartiallyPaid))
            .Select(i => new { Balance = i.Total - i.AmountPaid, i.DueDate }).ToListAsync(ct);
        var orderLines = await db.SalesOrderLines.AsNoTracking()
            .Join(db.SalesOrders, l => l.SalesOrderId, o => o.Id, (l, o) => new { l, o })
            .Where(x => x.o.CustomerId == customerId && x.o.Id != exclude &&
                        (x.o.Status == SoStatus.Confirmed || x.o.Status == SoStatus.PartiallyShipped
                         || x.o.Status == SoStatus.Shipped || x.o.Status == SoStatus.Closed))
            .Select(x => new { x.o.Status, x.l.Quantity, x.l.Factor, x.l.ShippedBase, x.l.InvoicedBase, x.l.UnitPrice, x.l.DiscountPercent, x.l.TaxRate })
            .ToListAsync(ct);
        var openOrders = orderLines.Sum(l =>
        {
            var billable = l.Status is SoStatus.Confirmed or SoStatus.PartiallyShipped ? l.Quantity * l.Factor : l.ShippedBase;
            var owed = Math.Max(0, billable - l.InvoicedBase);
            return owed / l.Factor * l.UnitPrice * (1 - l.DiscountPercent / 100) * (1 + l.TaxRate / 100);
        });
        return new CreditCheck(customer.CreditLimit, invoices.Sum(i => i.Balance), Math.Round(openOrders, 2), thisOrder,
            invoices.Count(i => i.DueDate < today.AddDays(-OverdueHoldDays)));
    }

    async Task<SalesOrder> Lock(Guid id, CancellationToken ct)
    {
        var so = await db.SalesOrders.FromSql($"SELECT *, xmin FROM sales_orders WHERE id = {id} FOR UPDATE")
                     .AsAsyncEnumerable().FirstOrDefaultAsync(ct)
                 ?? throw AppException.NotFound("so_not_found");
        await db.Entry(so).Collection(s => s.Lines).LoadAsync(ct);
        await EnsureVisible(so, ct);
        return so;
    }

    public async Task<SalesOrder> Load(Guid id, CancellationToken ct)
    {
        var so = await db.SalesOrders.Include(s => s.Lines.OrderBy(l => l.Position)).FirstOrDefaultAsync(s => s.Id == id, ct)
                 ?? throw AppException.NotFound("so_not_found");
        await EnsureVisible(so, ct);
        return so;
    }

    async Task EnsureVisible(SalesOrder so, CancellationToken ct)
    {
        var allowed = await access.AllowedAsync(ct);
        if (allowed is not null && !allowed.Contains(so.WarehouseId)) throw AppException.NotFound("so_not_found");
    }

    static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
