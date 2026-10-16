using Microsoft.EntityFrameworkCore;
using Tallyard.Api.Common;
using Tallyard.Api.Infrastructure;
using Tallyard.Api.Modules.Catalog;

namespace Tallyard.Api.Modules.Inventory;

/// <summary>Converts a quantity in any of a product's units ("3 case") to base units (72 bottle).</summary>
public sealed class Quantities(AppDbContext db)
{
    readonly Dictionary<Guid, Product> cache = [];

    public async Task<Product> ProductAsync(Guid productId, CancellationToken ct)
    {
        if (cache.TryGetValue(productId, out var p)) return p;
        p = await db.Products.AsNoTracking().Include(x => x.Units).FirstOrDefaultAsync(x => x.Id == productId, ct)
            ?? throw AppException.NotFound("product_not_found");
        return cache[productId] = p;
    }

    public async Task<decimal> ToBaseAsync(Guid productId, decimal quantity, string? unit, CancellationToken ct)
    {
        var p = await ProductAsync(productId, ct);
        if (string.IsNullOrWhiteSpace(unit) || string.Equals(unit, p.BaseUnit, StringComparison.OrdinalIgnoreCase)) return quantity;
        var u = p.Units.FirstOrDefault(x => string.Equals(x.Name, unit, StringComparison.OrdinalIgnoreCase))
                ?? throw new AppException(422, "unknown_unit", ("unit", unit), ("sku", p.Sku));
        return quantity * u.Factor;
    }
}
