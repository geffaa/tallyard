using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Tallyard.Api.Common;
using Tallyard.Api.Infrastructure;

namespace Tallyard.Api.Modules.Inventory;

public record StockCell(Guid WarehouseId, decimal OnHand, decimal Reserved);
public record StockRow(Guid ProductId, string Sku, string Name, string BaseUnit, bool TrackBatches, decimal OnHand, decimal Reserved, decimal? Value, StockCell[] Cells);
public record StockQuery(int Page = 1, int PageSize = 50, string? Q = null, Guid? WarehouseId = null, string? Filter = null);

public record BatchStock(Guid BatchId, string Number, DateOnly? ExpiresOn, Guid WarehouseId, decimal OnHand);
public record ProductStock(Guid ProductId, string Sku, string Name, string BaseUnit, bool TrackBatches, StockWarehouse[] Warehouses, BatchStock[] Batches);
public record StockWarehouse(Guid WarehouseId, string Code, string Name, decimal OnHand, decimal Reserved, decimal Available, decimal? AvgCost);

public record MovementDto(
    Guid Id, DateTimeOffset CreatedAt, string Number, string Kind, string? Reason, string? Note, string? SourceType, Guid? SourceId,
    Guid WarehouseId, string Warehouse, string? Batch, decimal Quantity, decimal BalanceAfter, decimal? UnitCost, string? User);

public record AdjustmentLine(Guid ProductId, decimal Quantity, string? Unit, string? BatchNumber, DateOnly? ExpiresOn, decimal? UnitCost);
public record AdjustmentInput(Guid WarehouseId, string Reason, string? Note, AdjustmentLine[] Lines, string? IdempotencyKey);
public record OperationDto(Guid Id, string Number, string Kind, string? Reason, string? Note, DateTimeOffset CreatedAt, string? User, int Lines, string? Warehouse);

public static class StockEndpoints
{
    public static readonly string[] Reasons = ["opening", "count", "damaged", "expired", "found", "sample", "other"];

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/stock");

        // The stock matrix: products down, warehouses across. Only warehouses the user may see.
        g.MapGet("/", async ([AsParameters] StockQuery p, AppDbContext db, WarehouseAccess access, CurrentUser me, CancellationToken ct) =>
        {
            var allowed = await access.AllowedAsync(ct);
            var balances = db.StockBalances.AsNoTracking().Where(b => allowed == null || allowed.Contains(b.WarehouseId));
            if (p.WarehouseId is { } wid)
            {
                await access.EnsureAsync(wid, ct);
                balances = balances.Where(b => b.WarehouseId == wid);
            }

            var products = db.Products.AsNoTracking().Where(x => x.IsActive);
            if (!string.IsNullOrWhiteSpace(p.Q))
            {
                var term = p.Q.Trim();
                products = products.Where(x => x.Sku == term.ToUpper() || x.Barcodes.Any(b => b.Code == term)
                    || EF.Functions.ILike(x.Name, $"%{term}%") || EF.Functions.ILike(x.Sku, $"%{term}%"));
            }

            var rows = products.Select(x => new
            {
                x.Id, x.Sku, x.Name, x.BaseUnit, x.TrackBatches,
                OnHand = balances.Where(b => b.ProductId == x.Id).Sum(b => (decimal?)b.OnHand) ?? 0,
                Reserved = balances.Where(b => b.ProductId == x.Id).Sum(b => (decimal?)b.Reserved) ?? 0,
                Value = balances.Where(b => b.ProductId == x.Id).Sum(b => (decimal?)(b.OnHand * b.AvgCost)) ?? 0,
            });
            rows = p.Filter switch
            {
                "out" => rows.Where(r => r.OnHand - r.Reserved <= 0),
                "in" => rows.Where(r => r.OnHand > 0),
                _ => rows,
            };

            var page = await rows.OrderBy(r => r.Sku).ToPageAsync(new PageQuery(p.Page, p.PageSize), ct);
            var ids = page.Items.Select(r => r.Id).ToList();
            var cells = (await balances.Where(b => ids.Contains(b.ProductId)).ToListAsync(ct)).ToLookup(b => b.ProductId);
            var costs = Perm.Has(me.Role!.Value, Perm.ViewCosts);
            return new Page<StockRow>(page.Items.Select(r => new StockRow(
                r.Id, r.Sku, r.Name, r.BaseUnit, r.TrackBatches, r.OnHand, r.Reserved, costs ? Math.Round(r.Value, 2) : null,
                cells[r.Id].Select(c => new StockCell(c.WarehouseId, c.OnHand, c.Reserved)).ToArray())).ToList(),
                page.Total, page.PageNumber, page.PageSize);
        });

        g.MapGet("/{productId:guid}", async (Guid productId, AppDbContext db, WarehouseAccess access, CurrentUser me, CancellationToken ct) =>
        {
            var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.Id == productId, ct)
                          ?? throw AppException.NotFound("product_not_found");
            var allowed = await access.AllowedAsync(ct);
            var warehouses = await db.Warehouses.AsNoTracking()
                .Where(w => w.IsActive && (allowed == null || allowed.Contains(w.Id))).OrderBy(w => w.Code).ToListAsync(ct);
            var ids = warehouses.Select(w => w.Id).ToList();
            var balances = await db.StockBalances.AsNoTracking()
                .Where(b => b.ProductId == productId && ids.Contains(b.WarehouseId)).ToDictionaryAsync(b => b.WarehouseId, ct);
            var costs = Perm.Has(me.Role!.Value, Perm.ViewCosts);
            var batches = await db.BatchBalances.AsNoTracking()
                .Where(b => b.ProductId == productId && ids.Contains(b.WarehouseId) && b.OnHand > 0)
                .Join(db.Batches, bb => bb.BatchId, b => b.Id, (bb, b) => new { bb, b })
                .OrderBy(x => x.b.ExpiresOn).ThenBy(x => x.b.Number)
                .Select(x => new BatchStock(x.b.Id, x.b.Number, x.b.ExpiresOn, x.bb.WarehouseId, x.bb.OnHand))
                .ToArrayAsync(ct);
            return new ProductStock(product.Id, product.Sku, product.Name, product.BaseUnit, product.TrackBatches,
                warehouses.Select(w =>
                {
                    var b = balances.GetValueOrDefault(w.Id);
                    return new StockWarehouse(w.Id, w.Code, w.Name, b?.OnHand ?? 0, b?.Reserved ?? 0, b?.Available ?? 0, costs ? b?.AvgCost : null);
                }).ToArray(),
                batches);
        });

        // The stock card: every movement of one product, with the running balance per warehouse.
        g.MapGet("/{productId:guid}/movements", async (Guid productId, Guid? warehouseId, int? page, AppDbContext db,
            WarehouseAccess access, CurrentUser me, CancellationToken ct) =>
        {
            var allowed = await access.AllowedAsync(ct);
            if (warehouseId is { } w) await access.EnsureAsync(w, ct);
            var costs = Perm.Has(me.Role!.Value, Perm.ViewCosts);
            var q = from m in db.StockMovements.AsNoTracking()
                    where m.ProductId == productId && (warehouseId == null || m.WarehouseId == warehouseId)
                          && (allowed == null || allowed.Contains(m.WarehouseId))
                    join o in db.StockOperations on m.OperationId equals o.Id
                    join wh in db.Warehouses on m.WarehouseId equals wh.Id
                    orderby m.CreatedAt descending, m.Id descending
                    select new MovementDto(m.Id, m.CreatedAt, o.Number, o.Kind.ToString(), o.Reason, o.Note, o.SourceType, o.SourceId,
                        m.WarehouseId, wh.Code,
                        db.Batches.Where(b => b.Id == m.BatchId).Select(b => b.Number).FirstOrDefault(),
                        m.Quantity, m.BalanceAfter, costs ? m.UnitCost : null,
                        db.Users.Where(u => u.Id == o.CreatedBy).Select(u => u.Name).FirstOrDefault());
            return await q.ToPageAsync(new PageQuery(page ?? 1, 50), ct);
        });

        g.MapGet("/adjustments", async (int? page, AppDbContext db, WarehouseAccess access, CancellationToken ct) =>
        {
            var allowed = await access.AllowedAsync(ct);
            var q = db.StockOperations.AsNoTracking()
                .Where(o => o.Kind == OperationKind.Adjustment)
                .Where(o => allowed == null || o.Movements.All(m => allowed.Contains(m.WarehouseId)))
                .OrderByDescending(o => o.CreatedAt)
                .Select(o => new OperationDto(o.Id, o.Number, o.Kind.ToString(), o.Reason, o.Note, o.CreatedAt,
                    db.Users.Where(u => u.Id == o.CreatedBy).Select(u => u.Name).FirstOrDefault(),
                    o.Movements.Select(m => m.ProductId).Distinct().Count(),
                    db.Warehouses.Where(w => w.Id == o.Movements.Select(m => m.WarehouseId).FirstOrDefault()).Select(w => w.Code).FirstOrDefault()));
            return await q.ToPageAsync(new PageQuery(page ?? 1, 25), ct);
        });

        g.MapPost("/adjustments", async (AdjustmentInput input, StockLedger ledger, Quantities qty, WarehouseAccess access, CancellationToken ct) =>
        {
            await access.EnsureAsync(input.WarehouseId, ct);
            var changes = new List<StockChange>();
            foreach (var line in input.Lines)
            {
                var product = await qty.ProductAsync(line.ProductId, ct);
                var baseQty = await qty.ToBaseAsync(line.ProductId, line.Quantity, line.Unit, ct);
                Guid? batchId = null;
                if (product.TrackBatches && !string.IsNullOrWhiteSpace(line.BatchNumber))
                    batchId = (await ledger.BatchAsync(line.ProductId, line.BatchNumber, line.ExpiresOn, ct)).Id;
                var unitCost = line.UnitCost is { } c ? c / (baseQty / line.Quantity) : (decimal?)null; // cost was per chosen unit
                changes.Add(new StockChange(line.ProductId, input.WarehouseId, baseQty, batchId, baseQty > 0 ? unitCost : null));
            }
            var op = await ledger.PostAsync(new PostRequest(OperationKind.Adjustment, "ADJ", changes,
                input.IdempotencyKey, input.Reason, input.Note), ct);
            return Results.Ok(new { op.Id, op.Number });
        }).RequireAuthorization(Perm.AdjustStock).Validated<AdjustmentInput>();
    }
}

public sealed class AdjustmentInputValidator : AbstractValidator<AdjustmentInput>
{
    public AdjustmentInputValidator()
    {
        RuleFor(x => x.WarehouseId).NotEmpty();
        RuleFor(x => x.Reason).Must(r => StockEndpoints.Reasons.Contains(r));
        RuleFor(x => x.Note).MaximumLength(500);
        RuleFor(x => x.Note).NotEmpty().When(x => x.Reason == "other");
        RuleFor(x => x.Lines).NotEmpty();
        RuleForEach(x => x.Lines).ChildRules(l =>
        {
            l.RuleFor(x => x.ProductId).NotEmpty();
            l.RuleFor(x => x.Quantity).NotEqual(0);
            l.RuleFor(x => x.UnitCost).GreaterThanOrEqualTo(0);
            l.RuleFor(x => x.BatchNumber).MaximumLength(40);
        });
        RuleFor(x => x.IdempotencyKey).MaximumLength(100);
    }
}
