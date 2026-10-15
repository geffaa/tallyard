using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Tallyard.Api.Common;
using Tallyard.Api.Infrastructure;

namespace Tallyard.Api.Modules.Catalog;

public record CategoryDto(Guid Id, string Name);
public record CategoryInput(string Name);

public record UnitDto(string Name, decimal Factor);
public record BarcodeDto(string Code, string? Unit);

public record ProductListItem(Guid Id, string Sku, string Name, string? Category, string BaseUnit, decimal SalePrice, bool TrackBatches, bool IsActive);

public record ProductDto(
    Guid Id, string Sku, string Name, string? Description, Guid? CategoryId, string BaseUnit,
    bool TrackBatches, decimal TaxRate, decimal SalePrice, bool IsActive,
    UnitDto[] Units, BarcodeDto[] Barcodes, uint Version);

public record ProductInput(
    string Sku, string Name, string? Description, Guid? CategoryId, string BaseUnit,
    bool TrackBatches, decimal TaxRate, decimal SalePrice, bool IsActive,
    UnitDto[]? Units, BarcodeDto[]? Barcodes, uint? Version)
{
    [JsonIgnore] public UnitDto[] UnitList => Units ?? [];
    [JsonIgnore] public BarcodeDto[] BarcodeList => Barcodes ?? [];
}

public record ProductQuery(int Page = 1, int PageSize = 25, string? Q = null, Guid? CategoryId = null, bool? Active = null);

public static class CatalogEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var c = api.MapGroup("/categories");
        c.MapGet("/", (AppDbContext db, CancellationToken ct) =>
            db.Categories.AsNoTracking().OrderBy(x => x.Name).Select(x => new CategoryDto(x.Id, x.Name)).ToListAsync(ct));
        c.MapPost("/", async (CategoryInput input, AppDbContext db, CancellationToken ct) =>
        {
            var name = input.Name.Trim();
            var existing = await db.Categories.FirstOrDefaultAsync(x => x.Name == name, ct);
            if (existing is not null) return Results.Ok(new CategoryDto(existing.Id, existing.Name));
            var cat = new Category { Name = name };
            db.Categories.Add(cat);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/categories/{cat.Id}", new CategoryDto(cat.Id, cat.Name));
        }).RequireAuthorization(Perm.EditCatalog).Validated<CategoryInput>();

        var g = api.MapGroup("/products");

        g.MapGet("/", async ([AsParameters] ProductQuery p, AppDbContext db, CancellationToken ct) =>
        {
            var q = db.Products.AsNoTracking();
            if (p.CategoryId is { } cat) q = q.Where(x => x.CategoryId == cat);
            if (p.Active is { } active) q = q.Where(x => x.IsActive == active);
            var page = new PageQuery(p.Page, p.PageSize);
            if (string.IsNullOrWhiteSpace(p.Q))
                return await q.OrderBy(x => x.Sku).Select(ListItem).ToPageAsync(page, ct);

            // Exact SKU or barcode, names containing the text, or names similar enough to
            // survive a typo ("choclate" finds "Chocolate"); best matches first.
            var term = p.Q.Trim();
            return await q.Where(x => x.Sku == term.ToUpper()
                                      || x.Barcodes.Any(b => b.Code == term)
                                      || EF.Functions.ILike(x.Name, $"%{term}%")
                                      || EF.Functions.ILike(x.Sku, $"%{term}%")
                                      || EF.Functions.TrigramsWordSimilarity(term, x.Name) > 0.5)
                .OrderByDescending(x => x.Sku == term.ToUpper() || x.Barcodes.Any(b => b.Code == term))
                .ThenByDescending(x => EF.Functions.TrigramsWordSimilarity(term, x.Name))
                .ThenBy(x => x.Sku)
                .Select(ListItem).ToPageAsync(page, ct);
        });

        g.MapGet("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var p = await db.Products.AsNoTracking().Include(x => x.Units).Include(x => x.Barcodes)
                        .FirstOrDefaultAsync(x => x.Id == id, ct)
                    ?? throw AppException.NotFound("product_not_found");
            return ToDto(p);
        });

        g.MapPost("/", async (ProductInput input, AppDbContext db, CancellationToken ct) =>
        {
            var sku = input.Sku.Trim().ToUpperInvariant();
            if (await db.Products.AnyAsync(x => x.Sku == sku, ct)) throw AppException.Conflict("sku_taken", ("sku", sku));
            var p = new Product { Sku = sku, Name = input.Name.Trim() };
            await Apply(db, p, input, ct);
            db.Products.Add(p);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/products/{p.Id}", ToDto(p));
        }).RequireAuthorization(Perm.EditCatalog).Validated<ProductInput>();

        g.MapPut("/{id:guid}", async (Guid id, ProductInput input, AppDbContext db, CancellationToken ct) =>
        {
            var p = await db.Products.Include(x => x.Units).Include(x => x.Barcodes).FirstOrDefaultAsync(x => x.Id == id, ct)
                    ?? throw AppException.NotFound("product_not_found");
            var sku = input.Sku.Trim().ToUpperInvariant();
            if (sku != p.Sku && await db.Products.AnyAsync(x => x.Sku == sku, ct))
                throw AppException.Conflict("sku_taken", ("sku", sku));
            db.Entry(p).Property(x => x.Version).OriginalValue = input.Version ?? p.Version;
            p.Sku = sku;
            p.Name = input.Name.Trim();
            await Apply(db, p, input, ct);
            await db.SaveChangesAsync(ct);
            return ToDto(p);
        }).RequireAuthorization(Perm.EditCatalog).Validated<ProductInput>();
    }

    static async Task Apply(AppDbContext db, Product p, ProductInput input, CancellationToken ct)
    {
        if (input.CategoryId is { } cat && !await db.Categories.AnyAsync(x => x.Id == cat, ct))
            throw AppException.NotFound("category_not_found");
        p.Description = input.Description?.Trim();
        p.CategoryId = input.CategoryId;
        p.BaseUnit = input.BaseUnit.Trim().ToLowerInvariant();
        p.TrackBatches = input.TrackBatches;
        p.TaxRate = input.TaxRate;
        p.SalePrice = input.SalePrice;
        p.IsActive = input.IsActive;

        // Units: replace by name, keeping rows whose name stays.
        var units = input.UnitList.Select(u => u with { Name = u.Name.Trim().ToLowerInvariant() }).ToList();
        p.Units.RemoveAll(u => units.All(x => x.Name != u.Name));
        foreach (var u in units)
        {
            var row = p.Units.FirstOrDefault(x => x.Name == u.Name);
            if (row is null) p.Units.Add(new ProductUnit { ProductId = p.Id, Name = u.Name, Factor = u.Factor });
            else row.Factor = u.Factor;
        }

        var codes = input.BarcodeList.Select(b => b with { Code = b.Code.Trim(), Unit = b.Unit?.Trim().ToLowerInvariant() }).ToList();
        var codeList = codes.Select(b => b.Code).ToList();
        var clash = await db.ProductBarcodes.AsNoTracking()
            .Where(b => codeList.Contains(b.Code) && b.ProductId != p.Id)
            .Join(db.Products, b => b.ProductId, x => x.Id, (b, x) => new { b.Code, x.Sku })
            .FirstOrDefaultAsync(ct);
        if (clash is not null) throw AppException.Conflict("barcode_taken", ("barcode", clash.Code), ("sku", clash.Sku));
        p.Barcodes.RemoveAll(b => codes.All(x => x.Code != b.Code));
        foreach (var b in codes)
        {
            var row = p.Barcodes.FirstOrDefault(x => x.Code == b.Code);
            if (row is null) p.Barcodes.Add(new ProductBarcode { ProductId = p.Id, Code = b.Code, Unit = b.Unit });
            else row.Unit = b.Unit;
        }
    }

    static readonly System.Linq.Expressions.Expression<Func<Product, ProductListItem>> ListItem = x =>
        new ProductListItem(x.Id, x.Sku, x.Name, x.Category!.Name, x.BaseUnit, x.SalePrice, x.TrackBatches, x.IsActive);

    static ProductDto ToDto(Product p) => new(
        p.Id, p.Sku, p.Name, p.Description, p.CategoryId, p.BaseUnit, p.TrackBatches, p.TaxRate, p.SalePrice, p.IsActive,
        p.Units.OrderBy(u => u.Factor).Select(u => new UnitDto(u.Name, u.Factor)).ToArray(),
        p.Barcodes.Select(b => new BarcodeDto(b.Code, b.Unit)).ToArray(), p.Version);
}

public sealed class CategoryInputValidator : AbstractValidator<CategoryInput>
{
    public CategoryInputValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(80);
}

public sealed class ProductInputValidator : AbstractValidator<ProductInput>
{
    public ProductInputValidator()
    {
        RuleFor(x => x.Sku).NotEmpty().MaximumLength(40).Matches("^[A-Za-z0-9._-]+$");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.BaseUnit).NotEmpty().MaximumLength(20);
        RuleFor(x => x.TaxRate).InclusiveBetween(0, 100);
        RuleFor(x => x.SalePrice).GreaterThanOrEqualTo(0);
        RuleForEach(x => x.UnitList).ChildRules(u =>
        {
            u.RuleFor(x => x.Name).NotEmpty().MaximumLength(20);
            u.RuleFor(x => x.Factor).GreaterThan(1);
        }).OverridePropertyName("units");
        RuleFor(x => x.UnitList).Must(u => u.Select(x => (x.Name ?? "").Trim().ToLowerInvariant()).Distinct().Count() == u.Length)
            .OverridePropertyName("units").WithMessage("Unit names must be unique");
        RuleFor(x => x).Must(x => x.UnitList.All(u => !string.Equals(u.Name?.Trim(), x.BaseUnit?.Trim(), StringComparison.OrdinalIgnoreCase)))
            .WithName("units").WithMessage("A unit can't have the same name as the base unit");
        RuleForEach(x => x.BarcodeList).ChildRules(b => b.RuleFor(x => x.Code).NotEmpty().MaximumLength(64))
            .OverridePropertyName("barcodes");
        RuleFor(x => x).Must(x => x.BarcodeList.All(b => b.Unit is null
                || string.Equals(b.Unit.Trim(), x.BaseUnit?.Trim(), StringComparison.OrdinalIgnoreCase)
                || x.UnitList.Any(u => string.Equals(u.Name?.Trim(), b.Unit.Trim(), StringComparison.OrdinalIgnoreCase))))
            .WithName("barcodes").WithMessage("A barcode's unit must be the base unit or one of the product's units");
        RuleFor(x => x.BarcodeList).Must(b => b.Select(x => (x.Code ?? "").Trim()).Distinct().Count() == b.Length)
            .OverridePropertyName("barcodes").WithMessage("Barcodes must be unique");
    }
}
