using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Tallyard.Api.Common;
using Tallyard.Api.Infrastructure;
using Tallyard.Api.Modules.Identity;

namespace Tallyard.Api.Modules.Purchasing;

public record PoListItem(
    Guid Id, string Number, string Supplier, string Warehouse, string Status, DateOnly OrderDate, DateOnly? ExpectedOn,
    int Lines, decimal? Total, bool AwaitingMe, decimal ReceivedPercent);

public record PoLineDto(
    Guid Id, Guid ProductId, string Sku, string Name, string BaseUnit, bool TrackBatches, string Unit, decimal Factor,
    decimal Quantity, decimal? UnitPrice, decimal? TaxRate, decimal? Net, decimal ReceivedBase, decimal OrderedBase);

public record ApprovalStep(int Level, string Role, decimal? MinTotal, string? ApprovedBy, DateTimeOffset? At, string? Comment);
public record ReceiptDto(Guid Id, string Number, DateTimeOffset ReceivedAt, string? ReceivedBy, int Lines);

public record PoDto(
    Guid Id, string Number, string Status, Guid SupplierId, string Supplier, Guid WarehouseId, string Warehouse,
    DateOnly OrderDate, DateOnly? ExpectedOn, string? Note, decimal? Subtotal, decimal? TaxTotal, decimal? Total,
    string? CreatedBy, Guid? CreatedById, DateTimeOffset CreatedAt, DateTimeOffset? SubmittedAt, DateTimeOffset? ApprovedAt,
    DateTimeOffset? SentAt, DateTimeOffset? ClosedAt, string? RejectedBy, string? RejectionReason, DateTimeOffset? RejectedAt,
    PoLineDto[] Lines, ApprovalStep[] Approvals, ReceiptDto[] Receipts, string[] Actions, uint Version);

public record CommentInput(string? Comment);
public record ApprovalRuleDto(int Level, decimal MinTotal, string Role);
public record ReorderRuleDto(Guid WarehouseId, Guid SupplierId, decimal ReorderPoint, decimal ReorderQuantity);

public static class PurchasingEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/purchase-orders").RequireAuthorization(Perm.ViewPurchasing);

        g.MapGet("/", async (string? status, string? q, int? page, AppDbContext db, WarehouseAccess access, CurrentUser me,
            PurchasingService svc, CancellationToken ct) =>
        {
            var allowed = await access.AllowedAsync(ct);
            var orders = db.PurchaseOrders.AsNoTracking().Where(p => allowed == null || allowed.Contains(p.WarehouseId));
            var costs = Perm.Has(me.Role!.Value, Perm.ViewCosts);
            var rules = await svc.Rules(ct);
            orders = status switch
            {
                "draft" => orders.Where(p => p.Status == PoStatus.Draft),
                "approval" or "mine" => orders.Where(p => p.Status == PoStatus.PendingApproval),
                "open" => orders.Where(p => p.Status == PoStatus.Approved || p.Status == PoStatus.Sent || p.Status == PoStatus.PartiallyReceived),
                "done" => orders.Where(p => p.Status == PoStatus.Received || p.Status == PoStatus.Closed || p.Status == PoStatus.Cancelled),
                _ => orders,
            };
            if (!string.IsNullOrWhiteSpace(q))
                orders = orders.Where(p => EF.Functions.ILike(p.Number, $"%{q}%")
                    || db.Suppliers.Any(s => s.Id == p.SupplierId && EF.Functions.ILike(s.Name, $"%{q}%")));
            var result = await orders.OrderByDescending(p => p.CreatedAt).Select(p => new
            {
                p.Id, p.Number, p.Status, p.OrderDate, p.ExpectedOn, p.Total, p.CreatedBy,
                Supplier = db.Suppliers.Where(s => s.Id == p.SupplierId).Select(s => s.Name).First(),
                Warehouse = db.Warehouses.Where(w => w.Id == p.WarehouseId).Select(w => w.Code).First(),
                Lines = p.Lines.Count,
                Ordered = p.Lines.Sum(l => l.Quantity * l.Factor),
                Received = p.Lines.Sum(l => l.ReceivedBase),
                Approved = p.Approvals.Select(a => a.Level).ToList(),
            }).ToPageAsync(new PageQuery(status == "mine" ? 1 : page ?? 1, status == "mine" ? 200 : 25), ct);
            var items = result.Items.Select(p => new PoListItem(
                p.Id, p.Number, p.Supplier, p.Warehouse, p.Status.ToString(), p.OrderDate, p.ExpectedOn, p.Lines,
                costs ? p.Total : null,
                AwaitingMe: p.Status == PoStatus.PendingApproval && p.CreatedBy != me.UserId &&
                            NextLevel(p.Total, p.Approved, rules) is { } next && (next.Role == me.Role || me.Role == Role.Owner),
                p.Ordered == 0 ? 0 : Math.Round(Math.Min(1, p.Received / p.Ordered) * 100, 0))).ToList();
            // "Needs my approval" depends on the rules and who approved what, so it is filtered here
            // (a handful of pending orders at most).
            if (status == "mine") items = items.Where(i => i.AwaitingMe).ToList();
            return new Page<PoListItem>(items, status == "mine" ? items.Count : result.Total, result.PageNumber, result.PageSize);
        });

        g.MapGet("/{id:guid}", async (Guid id, PurchasingService svc, AppDbContext db, CurrentUser me, CancellationToken ct) =>
            await Dto(await svc.Load(id, ct), db, me, svc, ct));

        g.MapPost("/", async (PoInput input, PurchasingService svc, AppDbContext db, CurrentUser me, CancellationToken ct) =>
        {
            var po = await svc.CreateAsync(input, ct);
            return Results.Created($"/api/purchase-orders/{po.Id}", await Dto(po, db, me, svc, ct));
        }).RequireAuthorization(Perm.EditPurchaseOrders).Validated<PoInput>();

        g.MapPut("/{id:guid}", async (Guid id, PoInput input, PurchasingService svc, AppDbContext db, CurrentUser me, CancellationToken ct) =>
            await Dto(await svc.UpdateAsync(id, input, ct), db, me, svc, ct))
            .RequireAuthorization(Perm.EditPurchaseOrders).Validated<PoInput>();

        g.MapPost("/{id:guid}/submit", async (Guid id, PurchasingService svc, AppDbContext db, CurrentUser me, CancellationToken ct) =>
            await Dto(await svc.SubmitAsync(id, ct), db, me, svc, ct)).RequireAuthorization(Perm.EditPurchaseOrders);

        g.MapPost("/{id:guid}/approve", async (Guid id, CommentInput input, PurchasingService svc, AppDbContext db, CurrentUser me, CancellationToken ct) =>
            await Dto(await svc.ApproveAsync(id, input.Comment, ct), db, me, svc, ct)).RequireAuthorization(Perm.ApprovePurchaseOrders);

        g.MapPost("/{id:guid}/reject", async (Guid id, CommentInput input, PurchasingService svc, AppDbContext db, CurrentUser me, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Comment)) throw new AppException(422, "reject_reason_required");
            return await Dto(await svc.RejectAsync(id, input.Comment.Trim(), ct), db, me, svc, ct);
        }).RequireAuthorization(Perm.ApprovePurchaseOrders);

        g.MapPost("/{id:guid}/send", async (Guid id, PurchasingService svc, AppDbContext db, CurrentUser me, CancellationToken ct) =>
            await Dto(await svc.TransitionAsync(id, (p, now) => p.MarkSent(now), ct), db, me, svc, ct)).RequireAuthorization(Perm.EditPurchaseOrders);

        g.MapPost("/{id:guid}/cancel", async (Guid id, PurchasingService svc, AppDbContext db, CurrentUser me, CancellationToken ct) =>
            await Dto(await svc.TransitionAsync(id, (p, now) => p.Cancel(now), ct), db, me, svc, ct)).RequireAuthorization(Perm.EditPurchaseOrders);

        g.MapPost("/{id:guid}/close", async (Guid id, PurchasingService svc, AppDbContext db, CurrentUser me, CancellationToken ct) =>
            await Dto(await svc.TransitionAsync(id, (p, now) => p.Close(now), ct), db, me, svc, ct)).RequireAuthorization(Perm.EditPurchaseOrders);

        g.MapPost("/{id:guid}/receive", async (Guid id, ReceiveInput input, PurchasingService svc, CancellationToken ct) =>
        {
            var receipt = await svc.ReceiveAsync(id, input, ct);
            return Results.Ok(new { receipt.Id, receipt.Number });
        }).RequireAuthorization(Perm.ReceiveGoods).Validated<ReceiveInput>();

        g.MapGet("/{id:guid}/pdf", async (Guid id, PurchasingService svc, AppDbContext db, CancellationToken ct) =>
        {
            var po = await svc.Load(id, ct);
            if (po.Status is PoStatus.Draft or PoStatus.PendingApproval or PoStatus.Cancelled) throw AppException.Conflict("po_not_approved");
            var tenant = await db.Tenants.AsNoTracking().FirstAsync(ct);
            var supplier = await db.Suppliers.AsNoTracking().FirstAsync(s => s.Id == po.SupplierId, ct);
            var warehouse = await db.Warehouses.AsNoTracking().FirstAsync(w => w.Id == po.WarehouseId, ct);
            var ids = po.Lines.Select(l => l.ProductId).ToList();
            var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
            var bytes = DocumentPdf.Render(new PdfDoc("Purchase order", po.Number, tenant.Currency,
                new PdfParty(tenant.Name, null, null, null, null), "Supplier",
                new PdfParty(supplier.Name, supplier.Address, supplier.Email, supplier.Phone, supplier.TaxId),
                "Deliver to", string.Join(", ", new[] { warehouse.Name, warehouse.Address, warehouse.City }.Where(x => !string.IsNullOrWhiteSpace(x))),
                [("Order date", po.OrderDate.ToString("yyyy-MM-dd")), .. po.ExpectedOn is { } e ? new[] { ("Expected", e.ToString("yyyy-MM-dd")) } : [],
                 ("Payment terms", $"{supplier.PaymentTermsDays} days")],
                po.Note,
                po.Lines.Select(l => new PdfLine(products[l.ProductId].Sku, products[l.ProductId].Name, l.Quantity, l.Unit, l.UnitPrice, l.TaxRate, l.Net)).ToList(),
                po.Subtotal, po.TaxTotal, po.Total, $"Please quote {po.Number} on your invoice and delivery note."));
            return Results.File(bytes, "application/pdf", $"{po.Number}.pdf");
        }).RequireAuthorization(Perm.ViewCosts);

        // Approval thresholds.
        var rules = api.MapGroup("/approval-rules");
        rules.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
            (await db.ApprovalRules.AsNoTracking().OrderBy(r => r.Level).ToListAsync(ct))
            .Select(r => new ApprovalRuleDto(r.Level, r.MinTotal, r.Role.ToString())));
        rules.MapPut("/", async (ApprovalRuleDto[] input, AppDbContext db, CancellationToken ct) =>
        {
            var levels = input.OrderBy(r => r.MinTotal).ToList();
            if (levels.Any(r => r.MinTotal < 0 || !Enum.TryParse<Role>(r.Role, out _)))
                throw new AppException(422, "validation_failed");
            db.ApprovalRules.RemoveRange(db.ApprovalRules);
            db.ApprovalRules.AddRange(levels.Select((r, i) => new ApprovalRule { Level = i + 1, MinTotal = r.MinTotal, Role = Enum.Parse<Role>(r.Role) }));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization(Perm.ManageUsers);

        // Reorder.
        var reorder = api.MapGroup("/reorder").RequireAuthorization(Perm.ViewPurchasing);
        reorder.MapGet("/suggestions", async (Reorder r, CurrentUser me, CancellationToken ct) =>
            await r.SuggestAsync(Perm.Has(me.Role!.Value, Perm.ViewCosts), ct));
        reorder.MapPost("/create-orders", async (ReorderPick[] picks, Reorder r, PurchasingService svc, CancellationToken ct) =>
        {
            if (picks.Length == 0) throw new AppException(422, "validation_failed");
            var orders = await r.CreateDraftsAsync(picks, svc, ct);
            return orders.Select(o => new { o.Id, o.Number });
        }).RequireAuthorization(Perm.EditPurchaseOrders);
        reorder.MapGet("/rules/{productId:guid}", async (Guid productId, AppDbContext db, CancellationToken ct) =>
            (await db.ReorderRules.AsNoTracking().Where(r => r.ProductId == productId).ToListAsync(ct))
            .Select(r => new ReorderRuleDto(r.WarehouseId, r.SupplierId, r.ReorderPoint, r.ReorderQuantity)));
        reorder.MapPut("/rules/{productId:guid}", async (Guid productId, ReorderRuleDto[] input, AppDbContext db, CancellationToken ct) =>
        {
            if (input.Any(r => r.ReorderPoint < 0 || r.ReorderQuantity <= 0) || input.GroupBy(r => r.WarehouseId).Any(g => g.Count() > 1))
                throw new AppException(422, "validation_failed");
            var existing = await db.ReorderRules.Where(r => r.ProductId == productId).ToListAsync(ct);
            db.ReorderRules.RemoveRange(existing.Where(e => input.All(i => i.WarehouseId != e.WarehouseId)));
            foreach (var i in input)
            {
                var row = existing.FirstOrDefault(e => e.WarehouseId == i.WarehouseId);
                if (row is null) db.ReorderRules.Add(row = new ReorderRule { ProductId = productId, WarehouseId = i.WarehouseId });
                row.SupplierId = i.SupplierId;
                row.ReorderPoint = i.ReorderPoint;
                row.ReorderQuantity = i.ReorderQuantity;
            }
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization(Perm.EditPurchaseOrders);
    }

    static ApprovalRule? NextLevel(decimal total, List<int> approved, List<ApprovalRule> rules) =>
        PurchaseOrder.Required(total, rules).FirstOrDefault(r => !approved.Contains(r.Level));

    static async Task<PoDto> Dto(PurchaseOrder po, AppDbContext db, CurrentUser me, PurchasingService svc, CancellationToken ct)
    {
        var costs = Perm.Has(me.Role!.Value, Perm.ViewCosts);
        var supplier = await db.Suppliers.AsNoTracking().Where(s => s.Id == po.SupplierId).Select(s => s.Name).FirstAsync(ct);
        var warehouse = await db.Warehouses.AsNoTracking().Where(w => w.Id == po.WarehouseId).Select(w => w.Code).FirstAsync(ct);
        var ids = po.Lines.Select(l => l.ProductId).ToList();
        var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var receipts = await db.GoodsReceipts.AsNoTracking().Where(r => r.PurchaseOrderId == po.Id).OrderBy(r => r.ReceivedAt)
            .Select(r => new { r.Id, r.Number, r.ReceivedAt, r.ReceivedBy, Lines = r.Lines.Count }).ToListAsync(ct);
        var userIds = po.Approvals.Select(a => (Guid?)a.ApprovedBy).Append(po.CreatedBy).Append(po.RejectedBy)
            .Concat(receipts.Select(r => r.ReceivedBy)).Where(u => u != null).Select(u => u!.Value).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        string? Name(Guid? id) => id is { } x ? users.GetValueOrDefault(x) : null;

        // Every level the order needs (or got), approved or still waiting.
        var rules = await svc.Rules(ct);
        var steps = PurchaseOrder.Required(po.Total, rules)
            .Select(r => po.Approvals.FirstOrDefault(a => a.Level == r.Level) is { } a
                ? new ApprovalStep(r.Level, r.Role.ToString(), costs ? r.MinTotal : null, Name(a.ApprovedBy), a.At, a.Comment)
                : new ApprovalStep(r.Level, r.Role.ToString(), costs ? r.MinTotal : null, null, null, null))
            .ToArray();

        return new PoDto(po.Id, po.Number, po.Status.ToString(), po.SupplierId, supplier, po.WarehouseId, warehouse,
            po.OrderDate, po.ExpectedOn, po.Note, costs ? po.Subtotal : null, costs ? po.TaxTotal : null, costs ? po.Total : null,
            Name(po.CreatedBy), po.CreatedBy, po.CreatedAt, po.SubmittedAt, po.ApprovedAt, po.SentAt, po.ClosedAt,
            Name(po.RejectedBy), po.RejectionReason, po.RejectedAt,
            po.Lines.OrderBy(l => l.Position).Select(l =>
            {
                var p = products[l.ProductId];
                return new PoLineDto(l.Id, l.ProductId, p.Sku, p.Name, p.BaseUnit, p.TrackBatches, l.Unit, l.Factor, l.Quantity,
                    costs ? l.UnitPrice : null, costs ? l.TaxRate : null, costs ? l.Net : null, l.ReceivedBase, l.OrderedBase);
            }).ToArray(),
            steps,
            receipts.Select(r => new ReceiptDto(r.Id, r.Number, r.ReceivedAt, Name(r.ReceivedBy), r.Lines)).ToArray(),
            Actions(po, me, rules), po.Version);
    }

    /// <summary>What the current user can do with this order now; the client shows exactly these buttons.</summary>
    static string[] Actions(PurchaseOrder po, CurrentUser me, List<ApprovalRule> rules)
    {
        var role = me.Role!.Value;
        var edit = Perm.Has(role, Perm.EditPurchaseOrders);
        var list = new List<string>();
        if (po.Editable && edit) list.AddRange(["edit", "submit"]);
        if (po.Status == PoStatus.PendingApproval && Perm.Has(role, Perm.ApprovePurchaseOrders) && po.CreatedBy != me.UserId
            && NextLevel(po.Total, po.Approvals.Select(a => a.Level).ToList(), rules) is { } next
            && (next.Role == role || role == Role.Owner) && po.Approvals.All(a => a.ApprovedBy != me.UserId))
            list.AddRange(["approve", "reject"]);
        if (po.Status is PoStatus.Approved && edit) list.Add("send");
        if (po.Status is not (PoStatus.Draft or PoStatus.PendingApproval or PoStatus.Cancelled) && Perm.Has(role, Perm.ViewCosts)) list.Add("pdf");
        if (po.Receivable && Perm.Has(role, Perm.ReceiveGoods)) list.Add("receive");
        if (po.Status == PoStatus.PartiallyReceived && edit) list.Add("close");
        if (po.Status is PoStatus.Draft or PoStatus.PendingApproval or PoStatus.Approved or PoStatus.Sent && edit) list.Add("cancel");
        return list.ToArray();
    }
}

public sealed class PoInputValidator : AbstractValidator<PoInput>
{
    public PoInputValidator()
    {
        RuleFor(x => x.SupplierId).NotEmpty();
        RuleFor(x => x.WarehouseId).NotEmpty();
        RuleFor(x => x.Note).MaximumLength(1000);
        RuleFor(x => x.Lines).NotEmpty();
        RuleForEach(x => x.Lines).ChildRules(l =>
        {
            l.RuleFor(x => x.ProductId).NotEmpty();
            l.RuleFor(x => x.Quantity).GreaterThan(0);
            l.RuleFor(x => x.UnitPrice).GreaterThanOrEqualTo(0);
            l.RuleFor(x => x.TaxRate).InclusiveBetween(0, 100);
        });
    }
}

public sealed class ReceiveInputValidator : AbstractValidator<ReceiveInput>
{
    public ReceiveInputValidator()
    {
        RuleFor(x => x.Lines).NotEmpty();
        RuleForEach(x => x.Lines).ChildRules(l => l.RuleFor(x => x.Quantity).GreaterThanOrEqualTo(0));
        RuleFor(x => x.Note).MaximumLength(500);
    }
}
