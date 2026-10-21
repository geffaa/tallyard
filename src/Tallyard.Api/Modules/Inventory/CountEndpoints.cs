using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Tallyard.Api.Common;
using Tallyard.Api.Infrastructure;

namespace Tallyard.Api.Modules.Inventory;

public record CountStartInput(Guid WarehouseId, Guid? CategoryId, string? Note);
public record CountListItem(Guid Id, string Number, string Warehouse, string Status, int Lines, int Counted, int Variances, DateTimeOffset CreatedAt);
public record CountLineDto(Guid Id, Guid ProductId, string Sku, string Name, string BaseUnit, string? Batch, DateOnly? ExpiresOn,
    decimal Expected, decimal? Counted, decimal? Variance, string? CountedBy);
public record CountDto(Guid Id, string Number, Guid WarehouseId, string Warehouse, string? Category, string Status, string? Note,
    DateTimeOffset CreatedAt, string? StartedBy, DateTimeOffset? PostedAt, string? PostedBy, string? Adjustment, CountLineDto[] Lines);
public record CountSetInput(Guid LineId, decimal? Counted);
public record CountScanInput(string Code, decimal? Quantity);
public record ScanResult(Guid? LineId, string? Sku, decimal Added, decimal? Counted, CountLineDto[] Choose);

public static class CountEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/stock-counts").RequireAuthorization(Perm.AdjustStock);

        g.MapGet("/", async (AppDbContext db, WarehouseAccess access, CancellationToken ct) =>
        {
            var allowed = await access.AllowedAsync(ct);
            return await db.StockCounts.AsNoTracking().Where(c => allowed == null || allowed.Contains(c.WarehouseId))
                .OrderByDescending(c => c.CreatedAt).Take(50)
                .Select(c => new CountListItem(c.Id, c.Number, db.Warehouses.Where(w => w.Id == c.WarehouseId).Select(w => w.Code).First(),
                    c.Status.ToString(), c.Lines.Count, c.Lines.Count(l => l.Counted != null), c.Lines.Count(l => l.Counted != null && l.Counted != l.Expected), c.CreatedAt))
                .ToListAsync(ct);
        });

        // Starting a count snapshots the expected quantities: one line per product, or per batch
        // in stock for batch-tracked products.
        g.MapPost("/", async (CountStartInput input, AppDbContext db, DocumentNumbers numbers, WarehouseAccess access, CurrentUser me, CancellationToken ct) =>
        {
            await access.EnsureAsync(input.WarehouseId, ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var count = new StockCount
            {
                Number = await numbers.NextAsync("CNT", ct), WarehouseId = input.WarehouseId, CategoryId = input.CategoryId,
                Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim(), StartedBy = me.UserId,
            };
            var products = await db.Products.AsNoTracking().Where(p => p.IsActive && (input.CategoryId == null || p.CategoryId == input.CategoryId))
                .Select(p => new { p.Id, p.TrackBatches }).ToListAsync(ct);
            var balances = await db.StockBalances.AsNoTracking().Where(b => b.WarehouseId == input.WarehouseId).ToDictionaryAsync(b => b.ProductId, b => b.OnHand, ct);
            var batches = (await db.BatchBalances.AsNoTracking().Where(b => b.WarehouseId == input.WarehouseId && b.OnHand > 0).ToListAsync(ct))
                .ToLookup(b => b.ProductId);
            foreach (var p in products)
            {
                if (p.TrackBatches)
                    foreach (var b in batches[p.Id])
                        count.Lines.Add(new StockCountLine { StockCountId = count.Id, ProductId = p.Id, BatchId = b.BatchId, Expected = b.OnHand });
                else
                    count.Lines.Add(new StockCountLine { StockCountId = count.Id, ProductId = p.Id, Expected = balances.GetValueOrDefault(p.Id) });
            }
            db.StockCounts.Add(count);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/stock-counts/{count.Id}", new { count.Id, count.Number });
        }).Validated<CountStartInput>();

        g.MapGet("/{id:guid}", async (Guid id, AppDbContext db, WarehouseAccess access, CancellationToken ct) => await Dto(db, access, id, ct));

        g.MapPost("/{id:guid}/set", async (Guid id, CountSetInput input, AppDbContext db, WarehouseAccess access, CurrentUser me, CancellationToken ct) =>
        {
            await Open(db, access, id, ct);
            if (input.Counted is < 0) throw new AppException(422, "validation_failed");
            var n = await db.StockCountLines.Where(l => l.Id == input.LineId && l.StockCountId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.Counted, input.Counted).SetProperty(l => l.CountedBy, me.UserId)
                    .SetProperty(l => l.CountedAt, DateTimeOffset.UtcNow), ct);
            return n == 0 ? throw AppException.NotFound("count_line_not_found") : Results.NoContent();
        });

        // One scan adds what the barcode stands for (a case barcode adds a case). The increment
        // happens in the database, so two people scanning the same shelf don't overwrite each other.
        g.MapPost("/{id:guid}/scan", async (Guid id, CountScanInput input, AppDbContext db, WarehouseAccess access, CurrentUser me, CancellationToken ct) =>
        {
            await Open(db, access, id, ct);
            var code = input.Code.Trim();
            var hit = await db.ProductBarcodes.AsNoTracking().Where(b => b.Code == code)
                .Join(db.Products, b => b.ProductId, p => p.Id, (b, p) => new { p.Id, p.Sku, p.BaseUnit, b.Unit }).FirstOrDefaultAsync(ct)
                ?? await db.Products.AsNoTracking().Where(p => p.Sku == code.ToUpper()).Select(p => new { p.Id, p.Sku, p.BaseUnit, Unit = (string?)null }).FirstOrDefaultAsync(ct)
                ?? throw new AppException(404, "barcode_unknown", ("code", code));
            var factor = hit.Unit is null || hit.Unit == hit.BaseUnit ? 1
                : await db.ProductUnits.AsNoTracking().Where(u => u.ProductId == hit.Id && u.Name == hit.Unit).Select(u => u.Factor).FirstOrDefaultAsync(ct);
            var add = (input.Quantity ?? 1) * (factor == 0 ? 1 : factor);
            var lines = await db.StockCountLines.AsNoTracking().Where(l => l.StockCountId == id && l.ProductId == hit.Id).Select(l => l.Id).ToListAsync(ct);
            if (lines.Count == 0) throw new AppException(404, "not_in_count", ("sku", hit.Sku));
            if (lines.Count > 1)
            {
                // Several batches: the counter picks which one is on the shelf.
                var all = (await Dto(db, access, id, ct)).Lines.Where(l => l.ProductId == hit.Id).ToArray();
                return new ScanResult(null, hit.Sku, add, null, all);
            }
            await db.StockCountLines.Where(l => l.Id == lines[0])
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.Counted, l => (l.Counted ?? 0) + add).SetProperty(l => l.CountedBy, me.UserId)
                    .SetProperty(l => l.CountedAt, DateTimeOffset.UtcNow), ct);
            var counted = await db.StockCountLines.AsNoTracking().Where(l => l.Id == lines[0]).Select(l => l.Counted).FirstAsync(ct);
            return new ScanResult(lines[0], hit.Sku, add, counted, []);
        });

        // Posting turns every counted difference into one adjustment (reason "count").
        g.MapPost("/{id:guid}/post", async (Guid id, AppDbContext db, StockLedger ledger, WarehouseAccess access, CurrentUser me, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var count = await db.StockCounts.FromSql($"SELECT *, xmin FROM stock_counts WHERE id = {id} FOR UPDATE").AsAsyncEnumerable().FirstOrDefaultAsync(ct)
                        ?? throw AppException.NotFound("count_not_found");
            await access.EnsureAsync(count.WarehouseId, ct);
            if (count.Status != CountStatus.Counting) throw AppException.Conflict("count_not_open");
            await db.Entry(count).Collection(c => c.Lines).LoadAsync(ct);
            var changes = count.Lines.Where(l => l.Counted is { } c && c != l.Expected)
                .Select(l => new StockChange(l.ProductId, count.WarehouseId, l.Counted!.Value - l.Expected, l.BatchId)).ToList();
            if (changes.Count > 0)
            {
                var op = await ledger.PostAsync(new PostRequest(OperationKind.Adjustment, "ADJ", changes, Reason: "count",
                    Note: $"Stock count {count.Number}", SourceType: nameof(StockCount), SourceId: count.Id), ct);
                count.AdjustmentId = op.Id;
            }
            count.Status = CountStatus.Posted;
            count.PostedBy = me.UserId;
            count.PostedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await ledger.PublishAsync(ct);
            return await Dto(db, access, id, ct);
        });

        g.MapPost("/{id:guid}/cancel", async (Guid id, AppDbContext db, WarehouseAccess access, CancellationToken ct) =>
        {
            var count = await Open(db, access, id, ct);
            count.Status = CountStatus.Cancelled;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    static async Task<StockCount> Open(AppDbContext db, WarehouseAccess access, Guid id, CancellationToken ct)
    {
        var count = await db.StockCounts.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw AppException.NotFound("count_not_found");
        await access.EnsureAsync(count.WarehouseId, ct);
        return count.Status == CountStatus.Counting ? count : throw AppException.Conflict("count_not_open");
    }

    static async Task<CountDto> Dto(AppDbContext db, WarehouseAccess access, Guid id, CancellationToken ct)
    {
        var c = await db.StockCounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw AppException.NotFound("count_not_found");
        await access.EnsureAsync(c.WarehouseId, ct);
        var lines = await (
            from l in db.StockCountLines.AsNoTracking().Where(l => l.StockCountId == id)
            join p in db.Products on l.ProductId equals p.Id
            from b in db.Batches.Where(b => b.Id == l.BatchId).DefaultIfEmpty()
            orderby p.Sku, b.ExpiresOn
            select new CountLineDto(l.Id, p.Id, p.Sku, p.Name, p.BaseUnit, b == null ? null : b.Number, b == null ? null : b.ExpiresOn,
                l.Expected, l.Counted, l.Counted == null ? null : l.Counted - l.Expected,
                db.Users.Where(u => u.Id == l.CountedBy).Select(u => u.Name).FirstOrDefault())).ToArrayAsync(ct);
        string? Name(Guid? u) => u is null ? null : db.Users.AsNoTracking().Where(x => x.Id == u).Select(x => x.Name).FirstOrDefault();
        return new CountDto(c.Id, c.Number, c.WarehouseId, await db.Warehouses.Where(w => w.Id == c.WarehouseId).Select(w => w.Code).FirstAsync(ct),
            c.CategoryId is null ? null : await db.Categories.Where(x => x.Id == c.CategoryId).Select(x => x.Name).FirstOrDefaultAsync(ct),
            c.Status.ToString(), c.Note, c.CreatedAt, Name(c.StartedBy), c.PostedAt, Name(c.PostedBy),
            c.AdjustmentId is null ? null : await db.StockOperations.Where(o => o.Id == c.AdjustmentId).Select(o => o.Number).FirstOrDefaultAsync(ct),
            lines);
    }
}

public sealed class CountStartInputValidator : AbstractValidator<CountStartInput>
{
    public CountStartInputValidator()
    {
        RuleFor(x => x.WarehouseId).NotEmpty();
        RuleFor(x => x.Note).MaximumLength(500);
    }
}
