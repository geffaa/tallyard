using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Tallyard.Api.Infrastructure;
using Tallyard.Api.Modules.Catalog;
using Tallyard.Api.Modules.Identity;
using Tallyard.Api.Modules.Partners;
using Tallyard.Api.Modules.Warehouses;

namespace Tallyard.Api.Seed;

/// <summary>
/// Fictional demo data: a US food and household distributor with three warehouses, and a small
/// second tenant used to show isolation. Deterministic (fixed random seed), and idempotent:
/// a tenant that already exists is left alone.
/// </summary>
public static class DemoSeeder
{
    public const string Password = "tallyard123";

    public static async Task RunAsync(IConfiguration config)
    {
        await Tenant(config, "harborpine", "Harbor & Pine Supply Co.", "USD", "harborpine.test",
            [("CHI", "Chicago DC", "Chicago"), ("DAL", "Dallas DC", "Dallas"), ("ATL", "Atlanta Depot", "Atlanta")],
            products: 400, suppliers: 12, customers: 40);
        await Tenant(config, "northpeak", "Northpeak Wholesale GmbH", "EUR", "northpeak.test",
            [("MUC", "Munich Warehouse", "Munich")],
            products: 30, suppliers: 3, customers: 8);
    }

    static async Task Tenant(IConfiguration config, string slug, string name, string currency, string domain,
        (string Code, string Name, string City)[] warehouses, int products, int suppliers, int customers)
    {
        var current = new CurrentUser();
        await using var db = Db.Owner(config, current);
        if (await db.Tenants.AnyAsync(t => t.Slug == slug)) return;

        var tenant = new Tenant { Slug = slug, Name = name, Currency = currency };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        current.Set(tenant.Id);

        var rng = new Random(slug.Sum(c => c)); // stable across runs, unlike string.GetHashCode
        var whs = warehouses.Select(w => new Warehouse { Code = w.Code, Name = w.Name, City = w.City }).ToList();
        db.Warehouses.AddRange(whs);

        var hasher = new PasswordHasher<User>();
        User U(string local, string fullName, Role role, bool all = true)
        {
            var u = new User { Email = $"{local}@{domain}", Name = fullName, Role = role, AllWarehouses = all };
            u.PasswordHash = hasher.HashPassword(u, Password);
            return u;
        }
        var warehouseUser = U("warehouse", "Marcus Webb", Role.Warehouse, all: false);
        warehouseUser.Warehouses.Add(new UserWarehouse { UserId = warehouseUser.Id, WarehouseId = whs[0].Id });
        db.Users.AddRange(
            U("owner", "Dana Whitfield", Role.Owner),
            U("purchasing", "Priya Raman", Role.Purchasing),
            warehouseUser,
            U("sales", "Leo Martins", Role.Sales),
            U("finance", "Hannah Cole", Role.Finance));

        var categories = Catalog.Categories.ToDictionary(c => c.Name, c => new Category { Name = c.Name });
        db.Categories.AddRange(categories.Values);

        var made = 0;
        var skus = new HashSet<string>();
        foreach (var (brand, cat, item, size, unit, perCase, price) in Catalog.Items(rng))
        {
            if (made >= products) break;
            var spec = Catalog.Categories.First(c => c.Name == cat);
            var sku = $"{spec.Prefix}-{brand[..3].ToUpperInvariant()}-{made + 1:D4}";
            if (!skus.Add(sku)) continue;
            var p = new Product
            {
                Sku = sku,
                Name = $"{brand} {item} {size}",
                CategoryId = categories[cat].Id,
                BaseUnit = unit,
                TrackBatches = spec.Perishable,
                TaxRate = currency == "EUR" ? 19m : 0m,
                SalePrice = price,
            };
            p.Units.Add(new ProductUnit { ProductId = p.Id, Name = "case", Factor = perCase });
            // GTIN-style codes: one for the unit, one for the case.
            p.Barcodes.Add(new ProductBarcode { ProductId = p.Id, Code = Gtin(rng), Unit = unit });
            p.Barcodes.Add(new ProductBarcode { ProductId = p.Id, Code = Gtin(rng), Unit = "case" });
            db.Products.Add(p);
            made++;
        }

        foreach (var (code, sname) in Catalog.SupplierNames.Take(suppliers).Select((n, i) => ($"SUP-{i + 1:D3}", n)))
            db.Suppliers.Add(new Supplier
            {
                Code = code, Name = sname, Email = $"orders@{Slug(sname)}.test",
                Phone = Phone(rng), LeadTimeDays = rng.Next(2, 15), PaymentTermsDays = new[] { 15, 30, 45 }[rng.Next(3)],
            });

        foreach (var (code, cname) in Catalog.CustomerNames.Take(customers).Select((n, i) => ($"CUS-{i + 1:D3}", n)))
            db.Customers.Add(new Customer
            {
                Code = code, Name = cname, Email = $"ap@{Slug(cname)}.test", Phone = Phone(rng),
                CreditLimit = rng.Next(0, 5) == 0 ? 0 : rng.Next(5, 80) * 1000m,
                PaymentTermsDays = new[] { 0, 14, 30 }[rng.Next(3)],
            });

        await db.SaveChangesAsync();
        Console.WriteLine($"Seeded {slug}: {whs.Count} warehouses, {made} products, {suppliers} suppliers, {customers} customers");
    }

    static string Gtin(Random rng)
    {
        var digits = Enumerable.Range(0, 12).Select(_ => rng.Next(10)).ToArray();
        digits[0] = 0;
        var sum = digits.Select((d, i) => i % 2 == 0 ? d : d * 3).Sum();
        return string.Concat(digits) + (10 - sum % 10) % 10;
    }

    static string Phone(Random rng) => $"+1 {rng.Next(200, 990)}-{rng.Next(200, 990)}-{rng.Next(1000, 9999)}";
    static string Slug(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).Take(18).ToArray());
}
