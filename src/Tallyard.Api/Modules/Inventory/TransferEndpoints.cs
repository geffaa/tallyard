using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Tallyard.Api.Common;
using Tallyard.Api.Infrastructure;

namespace Tallyard.Api.Modules.Inventory;

public record TransferLineInput(Guid ProductId, decimal Quantity, string? Unit, string? BatchNumber);
public record TransferInput(Guid FromWarehouseId, Guid ToWarehouseId, string? Note, TransferLineInput[] Lines, string? IdempotencyKey);

public record TransferListItem(Guid Id, string Number, string Status, string From, string To, int Lines, DateTimeOffset ShippedAt, DateTimeOffset? ReceivedAt);
public record TransferLineDto(Guid ProductId, string Sku, string Name, string BaseUnit, string? Batch, DateOnly? ExpiresOn, decimal Quantity);
public record TransferDto(
    Guid Id, string Number, string Status, Guid FromWarehouseId, string From, Guid ToWarehouseId, string To, string? Note,
    DateTimeOffset ShippedAt, string? ShippedBy, DateTimeOffset? ReceivedAt, string? ReceivedBy, TransferLineDto[] Lines, uint Version);

public static class TransferEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/transfers");

        g.MapGet("/", async (string? status, int? page, AppDbContext db, WarehouseAccess access, CancellationToken ct) =>
        {
            var allowed = await access.AllowedAsync(ct);
            var q = db.Transfers.AsNoTracking()
                .Where(t => allowed == null || allowed.Contains(t.FromWarehouseId) || allowed.Contains(t.ToWarehouseId));
            if (Enum.TryParse<TransferStatus>(status, out var s)) q = q.Where(t => t.Status == s);
            return await q.OrderByDescending(t => t.ShippedAt)
                .Select(t => new TransferListItem(t.Id, t.Number, t.Status.ToString(),
                    db.Warehouses.Where(w => w.Id == t.FromWarehouseId).Select(w => w.Code).First(),
                    db.Warehouses.Where(w => w.Id == t.ToWarehouseId).Select(w => w.Code).First(),
                    t.Lines.Count, t.ShippedAt, t.ReceivedAt))
                .ToPageAsync(new PageQuery(page ?? 1, 25), ct);
        });

        g.MapGet("/{id:guid}", async (Guid id, AppDbContext db, WarehouseAccess access, CancellationToken ct) =>
            await Load(db, access, id, ct));

        // Creating a transfer ships it: stock leaves the source now and is in transit until received.
        g.MapPost("/", async (TransferInput input, AppDbContext db, StockLedger ledger, Quantities qty,
            DocumentNumbers numbers, WarehouseAccess access, CurrentUser me, CancellationToken ct) =>
        {
            await access.EnsureAsync(input.FromWarehouseId, ct);
            if (!await db.Warehouses.AnyAsync(w => w.Id == input.ToWarehouseId && w.IsActive, ct))
                throw AppException.NotFound("warehouse_not_found");
            if (input.IdempotencyKey is { } key &&
                await db.StockOperations.AsNoTracking().FirstOrDefaultAsync(o => o.IdempotencyKey == key, ct) is { SourceId: { } done })
                return Results.Ok(await Load(db, access, done, ct));

            var changes = new List<StockChange>();
            foreach (var line in input.Lines)
            {
                var baseQty = await qty.ToBaseAsync(line.ProductId, line.Quantity, line.Unit, ct);
                Guid? batchId = null;
                if (!string.IsNullOrWhiteSpace(line.BatchNumber))
                {
                    var number = line.BatchNumber.Trim().ToUpperInvariant();
                    batchId = (await db.Batches.FirstOrDefaultAsync(b => b.ProductId == line.ProductId && b.Number == number, ct)
                               ?? throw new AppException(404, "batch_not_found", ("batch", number))).Id;
                }
                changes.Add(new StockChange(line.ProductId, input.FromWarehouseId, -baseQty, batchId));
            }

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var transfer = new Transfer
            {
                Number = await numbers.NextAsync("TRF", ct),
                FromWarehouseId = input.FromWarehouseId,
                ToWarehouseId = input.ToWarehouseId,
                Note = input.Note?.Trim(),
                ShippedBy = me.UserId,
            };
            var op = await ledger.PostAsync(new PostRequest(OperationKind.TransferOut, "TRF", changes, input.IdempotencyKey,
                Note: input.Note, SourceType: nameof(Transfer), SourceId: transfer.Id, Number: transfer.Number), ct);
            // One line per batch actually picked, at the cost it left with.
            transfer.Lines = op.Movements.Select(m => new TransferLine
            {
                TransferId = transfer.Id, ProductId = m.ProductId, BatchId = m.BatchId, Quantity = -m.Quantity, UnitCost = m.UnitCost,
            }).ToList();
            db.Transfers.Add(transfer);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await ledger.PublishAsync(ct);
            return Results.Created($"/api/transfers/{transfer.Id}", await Load(db, access, transfer.Id, ct));
        }).RequireAuthorization(Perm.TransferStock).Validated<TransferInput>();

        g.MapPost("/{id:guid}/receive", async (Guid id, AppDbContext db, StockLedger ledger, WarehouseAccess access,
            CurrentUser me, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // Lock the transfer so two people receiving at once can't both post the stock.
            var transfer = await db.Transfers.FromSql($"SELECT *, xmin FROM transfers WHERE id = {id} FOR UPDATE")
                               .AsAsyncEnumerable().FirstOrDefaultAsync(ct)
                           ?? throw AppException.NotFound("transfer_not_found");
            await db.Entry(transfer).Collection(t => t.Lines).LoadAsync(ct);
            await access.EnsureAsync(transfer.ToWarehouseId, ct);
            if (transfer.Status != TransferStatus.InTransit) throw AppException.Conflict("transfer_not_in_transit");

            await ledger.PostAsync(new PostRequest(OperationKind.TransferIn, "TRF",
                transfer.Lines.Select(l => new StockChange(l.ProductId, transfer.ToWarehouseId, l.Quantity, l.BatchId, l.UnitCost)).ToList(),
                SourceType: nameof(Transfer), SourceId: transfer.Id, Number: transfer.Number), ct);
            transfer.Status = TransferStatus.Received;
            transfer.ReceivedAt = DateTimeOffset.UtcNow;
            transfer.ReceivedBy = me.UserId;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await ledger.PublishAsync(ct);
            return await Load(db, access, id, ct);
        }).RequireAuthorization(Perm.TransferStock);
    }

    static async Task<TransferDto> Load(AppDbContext db, WarehouseAccess access, Guid id, CancellationToken ct)
    {
        var t = await db.Transfers.AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw AppException.NotFound("transfer_not_found");
        var allowed = await access.AllowedAsync(ct);
        if (allowed is not null && !allowed.Contains(t.FromWarehouseId) && !allowed.Contains(t.ToWarehouseId))
            throw AppException.NotFound("transfer_not_found");

        var wh = await db.Warehouses.AsNoTracking().Where(w => w.Id == t.FromWarehouseId || w.Id == t.ToWarehouseId)
            .ToDictionaryAsync(w => w.Id, ct);
        var productIds = t.Lines.Select(l => l.ProductId).ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var batchIds = t.Lines.Where(l => l.BatchId != null).Select(l => l.BatchId!.Value).ToList();
        var batches = await db.Batches.AsNoTracking().Where(b => batchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);
        var userIds = new[] { t.ShippedBy, t.ReceivedBy }.Where(u => u != null).ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, ct);

        return new TransferDto(t.Id, t.Number, t.Status.ToString(), t.FromWarehouseId, wh[t.FromWarehouseId].Code,
            t.ToWarehouseId, wh[t.ToWarehouseId].Code, t.Note, t.ShippedAt, t.ShippedBy is { } s ? users.GetValueOrDefault(s) : null,
            t.ReceivedAt, t.ReceivedBy is { } r ? users.GetValueOrDefault(r) : null,
            t.Lines.Select(l =>
            {
                var p = products[l.ProductId];
                var b = l.BatchId is { } bid ? batches[bid] : null;
                return new TransferLineDto(p.Id, p.Sku, p.Name, p.BaseUnit, b?.Number, b?.ExpiresOn, l.Quantity);
            }).ToArray(), t.Version);
    }
}

public sealed class TransferInputValidator : AbstractValidator<TransferInput>
{
    public TransferInputValidator()
    {
        RuleFor(x => x.FromWarehouseId).NotEmpty();
        RuleFor(x => x.ToWarehouseId).NotEmpty().NotEqual(x => x.FromWarehouseId).WithMessage("Choose a different destination");
        RuleFor(x => x.Lines).NotEmpty();
        RuleForEach(x => x.Lines).ChildRules(l =>
        {
            l.RuleFor(x => x.ProductId).NotEmpty();
            l.RuleFor(x => x.Quantity).GreaterThan(0);
        });
        RuleFor(x => x.Note).MaximumLength(500);
    }
}
