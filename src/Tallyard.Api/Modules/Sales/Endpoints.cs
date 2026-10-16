using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Tallyard.Api.Common;
using Tallyard.Api.Infrastructure;
using Tallyard.Api.Modules.Identity;

namespace Tallyard.Api.Modules.Sales;

public record SoListItem(
    Guid Id, string Number, string Customer, string Warehouse, string Status, DateOnly OrderDate, DateOnly? RequestedOn,
    int Lines, decimal? Total, decimal ShippedPercent, bool Backordered);

public record SoLineDto(
    Guid Id, Guid ProductId, string Sku, string Name, string BaseUnit, string Unit, decimal Factor, decimal Quantity,
    decimal? UnitPrice, decimal? DiscountPercent, decimal? TaxRate, decimal? Net,
    decimal Reserved, decimal Shipped, decimal Backordered, decimal Invoiced);

public record ShipmentSummary(Guid Id, string Number, DateTimeOffset ShippedAt, string? ShippedBy, int Lines, Guid? InvoiceId, string? InvoiceNumber);

public record SoDto(
    Guid Id, string Number, string Status, Guid CustomerId, string Customer, Guid WarehouseId, string Warehouse,
    DateOnly OrderDate, DateOnly? RequestedOn, string? CustomerReference, string? Note,
    decimal? Subtotal, decimal? TaxTotal, decimal? Total, string? CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset? ConfirmedAt,
    string? HoldReason, CreditDto? Credit, string? ReleasedBy, string? ReleaseNote, DateTimeOffset? ClosedAt,
    SoLineDto[] Lines, ShipmentSummary[] Shipments, string[] Actions, uint Version);

public record CreditDto(decimal Limit, decimal OpenInvoices, decimal OpenOrders, decimal ThisOrder, decimal Exposure, int OverdueInvoices);

public record PickLine(Guid LineId, string Sku, string Name, string Unit, decimal Factor, decimal Reserved, PickBatch[] Batches);
public record PickBatch(string? Batch, DateOnly? ExpiresOn, decimal QuantityBase);

public record InvoiceListItem(Guid Id, string Number, string Customer, DateOnly IssueDate, DateOnly DueDate, string Status, decimal Total, decimal Balance, int DaysOverdue);
public record InvoiceLineDto(string Description, decimal Quantity, string Unit, decimal UnitPrice, decimal DiscountPercent, decimal TaxRate, decimal Net);
public record InvoiceDto(
    Guid Id, string Number, Guid CustomerId, string Customer, Guid SalesOrderId, string SalesOrder, string Shipment,
    DateOnly IssueDate, DateOnly DueDate, string Status, decimal Subtotal, decimal TaxTotal, decimal Total, decimal AmountPaid, decimal Balance,
    InvoiceLineDto[] Lines, PaymentLine[] Payments);
public record PaymentLine(string Number, DateOnly ReceivedOn, string Method, decimal Amount);
public record ShipmentListItem(Guid Id, string Number, string SalesOrder, Guid SalesOrderId, string Customer, string Warehouse, DateTimeOffset ShippedAt, int Lines, string? Invoice);
public record AgingRow(Guid CustomerId, string Customer, decimal Current, decimal Days1To30, decimal Days31To60, decimal Days61To90, decimal Over90, decimal Total, decimal CreditLimit);
public record ConfirmInput(bool AllowBackorder);
public record NoteInput(string? Note);
public record InvoiceInput(DateOnly? IssueDate);

public static class SalesEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/sales-orders").RequireAuthorization(Perm.ViewSales);

        g.MapGet("/", async (string? status, string? q, Guid? customerId, int? page, AppDbContext db, WarehouseAccess access, CurrentUser me, CancellationToken ct) =>
        {
            var allowed = await access.AllowedAsync(ct);
            var orders = db.SalesOrders.AsNoTracking().Where(o => allowed == null || allowed.Contains(o.WarehouseId));
            if (customerId is { } c) orders = orders.Where(o => o.CustomerId == c);
            orders = status switch
            {
                "draft" => orders.Where(o => o.Status == SoStatus.Draft),
                "hold" => orders.Where(o => o.Status == SoStatus.OnHold),
                "open" => orders.Where(o => o.Status == SoStatus.Confirmed || o.Status == SoStatus.PartiallyShipped),
                "done" => orders.Where(o => o.Status == SoStatus.Shipped || o.Status == SoStatus.Closed || o.Status == SoStatus.Cancelled),
                _ => orders,
            };
            if (!string.IsNullOrWhiteSpace(q))
                orders = orders.Where(o => EF.Functions.ILike(o.Number, $"%{q}%") || EF.Functions.ILike(o.CustomerReference ?? "", $"%{q}%")
                    || db.Customers.Any(c => c.Id == o.CustomerId && EF.Functions.ILike(c.Name, $"%{q}%")));
            var prices = Perm.Has(me.Role!.Value, Perm.ViewSalesPrices);
            var page1 = await orders.OrderByDescending(o => o.CreatedAt).Select(o => new
            {
                o.Id, o.Number, o.Status, o.OrderDate, o.RequestedOn, o.Total,
                Customer = db.Customers.Where(c => c.Id == o.CustomerId).Select(c => c.Name).First(),
                Warehouse = db.Warehouses.Where(w => w.Id == o.WarehouseId).Select(w => w.Code).First(),
                Lines = o.Lines.Count,
                Ordered = o.Lines.Sum(l => l.Quantity * l.Factor),
                Shipped = o.Lines.Sum(l => l.ShippedBase),
                Reserved = o.Lines.Sum(l => l.ReservedBase),
            }).ToPageAsync(new PageQuery(page ?? 1, 25), ct);
            return new Page<SoListItem>(page1.Items.Select(o => new SoListItem(o.Id, o.Number, o.Customer, o.Warehouse, o.Status.ToString(),
                o.OrderDate, o.RequestedOn, o.Lines, prices ? o.Total : null,
                o.Ordered == 0 ? 0 : Math.Round(o.Shipped / o.Ordered * 100),
                o.Status is SoStatus.Confirmed or SoStatus.PartiallyShipped && o.Ordered - o.Shipped - o.Reserved > 0)).ToList(),
                page1.Total, page1.PageNumber, page1.PageSize);
        });

        g.MapGet("/{id:guid}", async (Guid id, SalesService svc, AppDbContext db, CurrentUser me, CancellationToken ct) =>
            await Dto(await svc.Load(id, ct), db, me, svc, ct));
        g.MapPost("/", async (SoInput input, SalesService svc, AppDbContext db, CurrentUser me, CancellationToken ct) =>
        {
            var so = await svc.CreateAsync(input, ct);
            return Results.Created($"/api/sales-orders/{so.Id}", await Dto(so, db, me, svc, ct));
        }).RequireAuthorization(Perm.EditSalesOrders).Validated<SoInput>();
        g.MapPut("/{id:guid}", async (Guid id, SoInput input, SalesService svc, AppDbContext db, CurrentUser me, CancellationToken ct) =>
            await Dto(await svc.UpdateAsync(id, input, ct), db, me, svc, ct)).RequireAuthorization(Perm.EditSalesOrders).Validated<SoInput>();
        g.MapPost("/{id:guid}/confirm", async (Guid id, ConfirmInput input, SalesService svc, AppDbContext db, CurrentUser me, CancellationToken ct) =>
            await Dto(await svc.ConfirmAsync(id, input.AllowBackorder, ct), db, me, svc, ct)).RequireAuthorization(Perm.EditSalesOrders);
        g.MapPost("/{id:guid}/release", async (Guid id, NoteInput input, SalesService svc, AppDbContext db, CurrentUser me, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Note)) throw new AppException(422, "release_note_required");
            return await Dto(await svc.ReleaseAsync(id, input.Note.Trim(), ct), db, me, svc, ct);
        }).RequireAuthorization(Perm.ReleaseCreditHolds);
        g.MapPost("/{id:guid}/reserve", async (Guid id, SalesService svc, AppDbContext db, CurrentUser me, CancellationToken ct) =>
            await Dto(await svc.ReserveAgainAsync(id, ct), db, me, svc, ct)).RequireAuthorization(Perm.EditSalesOrders);
        g.MapPost("/{id:guid}/cancel", async (Guid id, SalesService svc, AppDbContext db, CurrentUser me, CancellationToken ct) =>
            await Dto(await svc.CancelAsync(id, ct), db, me, svc, ct)).RequireAuthorization(Perm.EditSalesOrders);
        g.MapPost("/{id:guid}/close", async (Guid id, SalesService svc, AppDbContext db, CurrentUser me, CancellationToken ct) =>
            await Dto(await svc.CloseAsync(id, ct), db, me, svc, ct)).RequireAuthorization(Perm.EditSalesOrders);
        g.MapPost("/{id:guid}/ship", async (Guid id, ShipInput input, SalesService svc, CancellationToken ct) =>
        {
            var s = await svc.ShipAsync(id, input, ct);
            return Results.Ok(new { s.Id, s.Number });
        }).RequireAuthorization(Perm.ShipOrders).Validated<ShipInput>();

        // What to pick for each line: the reserved quantity, from the batches FEFO would take.
        g.MapGet("/{id:guid}/pick-list", async (Guid id, SalesService svc, AppDbContext db, CancellationToken ct) =>
        {
            var so = await svc.Load(id, ct);
            var ids = so.Lines.Select(l => l.ProductId).ToList();
            var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
            var batches = await db.BatchBalances.AsNoTracking().Where(b => ids.Contains(b.ProductId) && b.WarehouseId == so.WarehouseId && b.OnHand > 0)
                .Join(db.Batches, bb => bb.BatchId, b => b.Id, (bb, b) => new { bb.ProductId, b.Number, b.ExpiresOn, b.CreatedAt, bb.OnHand })
                .ToListAsync(ct);
            return so.Lines.Where(l => l.ReservedBase > 0).OrderBy(l => l.Position).Select(l =>
            {
                var p = products[l.ProductId];
                var picks = new List<PickBatch>();
                if (p.TrackBatches)
                {
                    var left = l.ReservedBase;
                    foreach (var b in batches.Where(b => b.ProductId == l.ProductId).OrderBy(b => b.ExpiresOn ?? DateOnly.MaxValue).ThenBy(b => b.CreatedAt))
                    {
                        if (left <= 0) break;
                        var take = Math.Min(left, b.OnHand);
                        picks.Add(new PickBatch(b.Number, b.ExpiresOn, take));
                        left -= take;
                    }
                }
                else picks.Add(new PickBatch(null, null, l.ReservedBase));
                return new PickLine(l.Id, p.Sku, p.Name, l.Unit, l.Factor, l.ReservedBase / l.Factor, picks.ToArray());
            }).ToList();
        });

        g.MapGet("/credit/{customerId:guid}", async (Guid customerId, decimal? amount, SalesService svc, CancellationToken ct) =>
        {
            var c = await svc.CheckCreditAsync(customerId, amount ?? 0, null, ct);
            return new CreditDto(c.Limit, c.OpenInvoices, c.OpenOrders, c.ThisOrder, c.Exposure, c.OverdueInvoices);
        }).RequireAuthorization(Perm.ViewSalesPrices);

        var shipments = api.MapGroup("/shipments").RequireAuthorization(Perm.ViewSales);
        shipments.MapGet("/", async (bool? uninvoiced, int? page, AppDbContext db, WarehouseAccess access, CancellationToken ct) =>
        {
            var allowed = await access.AllowedAsync(ct);
            var q = db.Shipments.AsNoTracking().Where(s => allowed == null || allowed.Contains(s.WarehouseId));
            if (uninvoiced == true) q = q.Where(s => s.InvoiceId == null);
            return await q.OrderByDescending(s => s.ShippedAt).Select(s => new ShipmentListItem(s.Id, s.Number,
                    db.SalesOrders.Where(o => o.Id == s.SalesOrderId).Select(o => o.Number).First(), s.SalesOrderId,
                    db.Customers.Where(c => c.Id == s.CustomerId).Select(c => c.Name).First(),
                    db.Warehouses.Where(w => w.Id == s.WarehouseId).Select(w => w.Code).First(),
                    s.ShippedAt, s.Lines.Count, db.Invoices.Where(i => i.Id == s.InvoiceId).Select(i => i.Number).FirstOrDefault()))
                .ToPageAsync(new PageQuery(page ?? 1, 25), ct);
        });
        shipments.MapPost("/{id:guid}/invoice", async (Guid id, InvoiceInput input, SalesService svc, CancellationToken ct) =>
        {
            var inv = await svc.InvoiceAsync(id, input.IssueDate, ct);
            return Results.Ok(new { inv.Id, inv.Number });
        }).RequireAuthorization(Perm.ManageReceivables);
        shipments.MapGet("/{id:guid}/pdf", async (Guid id, AppDbContext db, WarehouseAccess access, CancellationToken ct) =>
        {
            var s = await db.Shipments.AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id, ct)
                    ?? throw AppException.NotFound("shipment_not_found");
            await access.EnsureAsync(s.WarehouseId, ct);
            var so = await db.SalesOrders.AsNoTracking().Include(o => o.Lines).FirstAsync(o => o.Id == s.SalesOrderId, ct);
            var (tenant, customer, warehouse) = (await db.Tenants.AsNoTracking().FirstAsync(ct),
                await db.Customers.AsNoTracking().FirstAsync(c => c.Id == s.CustomerId, ct),
                await db.Warehouses.AsNoTracking().FirstAsync(w => w.Id == s.WarehouseId, ct));
            var products = await Products(db, s.Lines.Select(l => l.ProductId), ct);
            var batchIds = s.Lines.Where(l => l.BatchId != null).Select(l => l.BatchId!.Value).ToList();
            var batches = await db.Batches.AsNoTracking().Where(b => batchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);
            var lines = s.Lines.Select(l =>
            {
                var line = so.Lines.First(x => x.Id == l.SalesOrderLineId);
                var b = l.BatchId is { } bid ? batches[bid] : null;
                return new PdfLine(products[l.ProductId].Sku, products[l.ProductId].Name, l.QuantityBase / line.Factor, line.Unit, null, null, null,
                    b is null ? null : $"Batch {b.Number}" + (b.ExpiresOn is { } e ? $" · exp. {e:yyyy-MM-dd}" : ""));
            }).ToList();
            var pdf = DocumentPdf.Render(new PdfDoc("Delivery note", s.Number, tenant.Currency,
                new PdfParty(tenant.Name, $"{warehouse.Name}, {warehouse.City}", null, null, null), "Customer",
                new PdfParty(customer.Name, customer.Address, customer.Email, customer.Phone, null), null, null,
                [("Date", s.ShippedAt.ToString("yyyy-MM-dd")), ("Order", so.Number), .. so.CustomerReference is { } r ? new[] { ("Your ref.", r) } : [],
                 .. s.Carrier is { } c ? new[] { ("Carrier", c) } : [], .. s.TrackingNumber is { } t ? new[] { ("Tracking", t) } : []],
                null, lines, null, null, null, "Received in good condition: name, signature and date ______________________________"));
            return Results.File(pdf, "application/pdf", $"{s.Number}.pdf");
        });

        var invoices = api.MapGroup("/invoices").RequireAuthorization(Perm.ViewSalesPrices);
        invoices.MapGet("/", async (string? status, Guid? customerId, int? page, AppDbContext db, CancellationToken ct) =>
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var q = db.Invoices.AsNoTracking();
            if (customerId is { } c) q = q.Where(i => i.CustomerId == c);
            q = status switch
            {
                "open" => q.Where(i => i.Status == InvoiceStatus.Open || i.Status == InvoiceStatus.PartiallyPaid),
                "overdue" => q.Where(i => (i.Status == InvoiceStatus.Open || i.Status == InvoiceStatus.PartiallyPaid) && i.DueDate < today),
                "paid" => q.Where(i => i.Status == InvoiceStatus.Paid),
                _ => q,
            };
            var result = await q.OrderByDescending(i => i.IssueDate).ThenByDescending(i => i.Number).Select(i => new
            {
                i.Id, i.Number, i.IssueDate, i.DueDate, i.Status, i.Total, i.AmountPaid,
                Customer = db.Customers.Where(c => c.Id == i.CustomerId).Select(c => c.Name).First(),
            }).ToPageAsync(new PageQuery(page ?? 1, 25), ct);
            return new Page<InvoiceListItem>(result.Items.Select(i => new InvoiceListItem(i.Id, i.Number, i.Customer, i.IssueDate, i.DueDate,
                i.Status.ToString(), i.Total, i.Total - i.AmountPaid,
                i.Status is InvoiceStatus.Paid ? 0 : Math.Max(0, today.DayNumber - i.DueDate.DayNumber))).ToList(),
                result.Total, result.PageNumber, result.PageSize);
        });
        invoices.MapGet("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) => await InvoiceDto(db, id, ct));
        invoices.MapGet("/{id:guid}/pdf", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var inv = await InvoiceDto(db, id, ct);
            var tenant = await db.Tenants.AsNoTracking().FirstAsync(ct);
            var customer = await db.Customers.AsNoTracking().FirstAsync(c => c.Id == inv.CustomerId, ct);
            var pdf = DocumentPdf.Render(new PdfDoc("Invoice", inv.Number, tenant.Currency, new PdfParty(tenant.Name, null, null, null, null), "Bill to",
                new PdfParty(customer.Name, customer.Address, customer.Email, customer.Phone, customer.TaxId), null, null,
                [("Invoice date", inv.IssueDate.ToString("yyyy-MM-dd")), ("Due date", inv.DueDate.ToString("yyyy-MM-dd")),
                 ("Order", inv.SalesOrder), ("Delivery", inv.Shipment)],
                null,
                inv.Lines.Select(l => new PdfLine(l.Description.Split(' ')[0], l.Description[(l.Description.IndexOf(' ') + 1)..], l.Quantity, l.Unit,
                    l.UnitPrice, l.TaxRate, l.Net, l.DiscountPercent > 0 ? $"Discount {l.DiscountPercent:0.##}%" : null)).ToList(),
                inv.Subtotal, inv.TaxTotal, inv.Total, $"Please pay by {inv.DueDate:yyyy-MM-dd} quoting {inv.Number}.",
                inv.AmountPaid > 0 ? [("Paid", inv.AmountPaid.ToString("N2")), ("Balance due", inv.Balance.ToString("N2") + " " + tenant.Currency)] : null));
            return Results.File(pdf, "application/pdf", $"{inv.Number}.pdf");
        });

        var payments = api.MapGroup("/payments").RequireAuthorization(Perm.ManageReceivables);
        payments.MapGet("/", async (int? page, AppDbContext db, CancellationToken ct) =>
            await db.Payments.AsNoTracking().OrderByDescending(p => p.ReceivedOn).ThenByDescending(p => p.Number)
                .Select(p => new
                {
                    p.Id, p.Number, p.ReceivedOn, Method = p.Method.ToString(), p.Amount, p.Reference,
                    Customer = db.Customers.Where(c => c.Id == p.CustomerId).Select(c => c.Name).First(),
                    Invoices = p.Allocations.Select(a => db.Invoices.Where(i => i.Id == a.InvoiceId).Select(i => i.Number).First()).ToList(),
                }).ToPageAsync(new PageQuery(page ?? 1, 25), ct));
        payments.MapPost("/", async (PaymentInput input, SalesService svc, CancellationToken ct) =>
        {
            var p = await svc.PayAsync(input, ct);
            return Results.Ok(new { p.Id, p.Number });
        }).Validated<PaymentInput>();

        // Receivables by age: what each customer owes, by how long it is overdue.
        api.MapGet("/receivables/aging", async (AppDbContext db, CancellationToken ct) =>
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var open = await db.Invoices.AsNoTracking()
                .Where(i => i.Status == InvoiceStatus.Open || i.Status == InvoiceStatus.PartiallyPaid)
                .Select(i => new { i.CustomerId, Balance = i.Total - i.AmountPaid, i.DueDate }).ToListAsync(ct);
            var customers = await db.Customers.AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
            return open.GroupBy(i => i.CustomerId).Select(g =>
            {
                decimal Bucket(int from, int to) => g.Where(i => today.DayNumber - i.DueDate.DayNumber is var d && d >= from && d <= to).Sum(i => i.Balance);
                var c = customers[g.Key];
                return new AgingRow(c.Id, c.Name, Bucket(int.MinValue, 0), Bucket(1, 30), Bucket(31, 60), Bucket(61, 90), Bucket(91, int.MaxValue),
                    g.Sum(i => i.Balance), c.CreditLimit);
            }).OrderByDescending(r => r.Over90 + r.Days61To90).ThenByDescending(r => r.Total).ToList();
        }).RequireAuthorization(Perm.ViewSalesPrices);
    }

    static Task<Dictionary<Guid, Catalog.Product>> Products(AppDbContext db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return db.Products.AsNoTracking().Where(p => list.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
    }

    static async Task<InvoiceDto> InvoiceDto(AppDbContext db, Guid id, CancellationToken ct)
    {
        var i = await db.Invoices.AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw AppException.NotFound("invoice_not_found");
        var customer = await db.Customers.AsNoTracking().Where(c => c.Id == i.CustomerId).Select(c => c.Name).FirstAsync(ct);
        var so = await db.SalesOrders.AsNoTracking().Where(o => o.Id == i.SalesOrderId).Select(o => o.Number).FirstAsync(ct);
        var shipment = await db.Shipments.AsNoTracking().Where(s => s.Id == i.ShipmentId).Select(s => s.Number).FirstAsync(ct);
        var paid = await db.PaymentAllocations.AsNoTracking().Where(a => a.InvoiceId == id)
            .Join(db.Payments, a => a.PaymentId, p => p.Id, (a, p) => new PaymentLine(p.Number, p.ReceivedOn, p.Method.ToString(), a.Amount))
            .ToArrayAsync(ct);
        return new InvoiceDto(i.Id, i.Number, i.CustomerId, customer, i.SalesOrderId, so, shipment, i.IssueDate, i.DueDate, i.Status.ToString(),
            i.Subtotal, i.TaxTotal, i.Total, i.AmountPaid, i.Balance,
            i.Lines.Select(l => new InvoiceLineDto(l.Description, l.Quantity, l.Unit, l.UnitPrice, l.DiscountPercent, l.TaxRate, l.Net)).ToArray(), paid);
    }

    static async Task<SoDto> Dto(SalesOrder so, AppDbContext db, CurrentUser me, SalesService svc, CancellationToken ct)
    {
        var role = me.Role!.Value;
        var prices = Perm.Has(role, Perm.ViewSalesPrices);
        var customer = await db.Customers.AsNoTracking().Where(c => c.Id == so.CustomerId).Select(c => c.Name).FirstAsync(ct);
        var warehouse = await db.Warehouses.AsNoTracking().Where(w => w.Id == so.WarehouseId).Select(w => w.Code).FirstAsync(ct);
        var products = await Products(db, so.Lines.Select(l => l.ProductId), ct);
        var shipments = await db.Shipments.AsNoTracking().Where(s => s.SalesOrderId == so.Id).OrderBy(s => s.ShippedAt)
            .Select(s => new { s.Id, s.Number, s.ShippedAt, s.ShippedBy, Lines = s.Lines.Count, s.InvoiceId,
                Invoice = db.Invoices.Where(i => i.Id == s.InvoiceId).Select(i => i.Number).FirstOrDefault() })
            .ToListAsync(ct);
        var userIds = new[] { so.CreatedBy, so.ReleasedBy }.Concat(shipments.Select(s => s.ShippedBy)).Where(u => u != null).Select(u => u!.Value).ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        string? Name(Guid? id) => id is { } x ? users.GetValueOrDefault(x) : null;

        CreditDto? credit = null;
        if (prices && so.Status is SoStatus.Draft or SoStatus.OnHold)
        {
            var c = await svc.CheckCreditAsync(so.CustomerId, so.Total, so.Id, ct);
            credit = new CreditDto(c.Limit, c.OpenInvoices, c.OpenOrders, c.ThisOrder, c.Exposure, c.OverdueInvoices);
        }

        var actions = new List<string>();
        var edit = Perm.Has(role, Perm.EditSalesOrders);
        if (so.Status == SoStatus.Draft && edit) actions.AddRange(["edit", "confirm"]);
        if (so.Status == SoStatus.OnHold && Perm.Has(role, Perm.ReleaseCreditHolds)) actions.Add("release");
        if (so.Shippable && edit && so.Lines.Any(l => l.BackorderedBase > 0)) actions.Add("reserve");
        if (so.Shippable && Perm.Has(role, Perm.ShipOrders) && so.Lines.Any(l => l.ReservedBase > 0)) actions.Add("ship");
        if (so.Status == SoStatus.PartiallyShipped && edit) actions.Add("close");
        if (so.Status is SoStatus.Draft or SoStatus.OnHold or SoStatus.Confirmed && edit) actions.Add("cancel");
        if (shipments.Any(s => s.InvoiceId == null) && Perm.Has(role, Perm.ManageReceivables)) actions.Add("invoice");

        return new SoDto(so.Id, so.Number, so.Status.ToString(), so.CustomerId, customer, so.WarehouseId, warehouse, so.OrderDate, so.RequestedOn,
            so.CustomerReference, so.Note, prices ? so.Subtotal : null, prices ? so.TaxTotal : null, prices ? so.Total : null,
            Name(so.CreatedBy), so.CreatedAt, so.ConfirmedAt, so.HoldReason, credit, Name(so.ReleasedBy), so.ReleaseNote, so.ClosedAt,
            so.Lines.OrderBy(l => l.Position).Select(l =>
            {
                var p = products[l.ProductId];
                return new SoLineDto(l.Id, l.ProductId, p.Sku, p.Name, p.BaseUnit, l.Unit, l.Factor, l.Quantity,
                    prices ? l.UnitPrice : null, prices ? l.DiscountPercent : null, prices ? l.TaxRate : null, prices ? l.Net : null,
                    l.ReservedBase / l.Factor, l.ShippedBase / l.Factor, l.BackorderedBase / l.Factor, l.InvoicedBase / l.Factor);
            }).ToArray(),
            shipments.Select(s => new ShipmentSummary(s.Id, s.Number, s.ShippedAt, Name(s.ShippedBy), s.Lines, s.InvoiceId, s.Invoice)).ToArray(),
            actions.ToArray(), so.Version);
    }
}

public sealed class SoInputValidator : AbstractValidator<SoInput>
{
    public SoInputValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.WarehouseId).NotEmpty();
        RuleFor(x => x.CustomerReference).MaximumLength(60);
        RuleFor(x => x.Note).MaximumLength(1000);
        RuleFor(x => x.Lines).NotEmpty();
        RuleForEach(x => x.Lines).ChildRules(l =>
        {
            l.RuleFor(x => x.ProductId).NotEmpty();
            l.RuleFor(x => x.Quantity).GreaterThan(0);
            l.RuleFor(x => x.UnitPrice).GreaterThanOrEqualTo(0);
            l.RuleFor(x => x.DiscountPercent).InclusiveBetween(0, 100);
            l.RuleFor(x => x.TaxRate).InclusiveBetween(0, 100);
        });
    }
}

public sealed class ShipInputValidator : AbstractValidator<ShipInput>
{
    public ShipInputValidator()
    {
        RuleFor(x => x.Lines).NotEmpty();
        RuleForEach(x => x.Lines).ChildRules(l => l.RuleFor(x => x.Quantity).GreaterThanOrEqualTo(0));
        RuleFor(x => x.Carrier).MaximumLength(80);
        RuleFor(x => x.TrackingNumber).MaximumLength(80);
    }
}

public sealed class PaymentInputValidator : AbstractValidator<PaymentInput>
{
    public PaymentInputValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.Amount).GreaterThan(0);
        RuleFor(x => x.Method).IsEnumName(typeof(PaymentMethod));
        RuleFor(x => x.Reference).MaximumLength(80);
        RuleForEach(x => x.Allocations).ChildRules(a => a.RuleFor(x => x.Amount).GreaterThan(0));
    }
}
