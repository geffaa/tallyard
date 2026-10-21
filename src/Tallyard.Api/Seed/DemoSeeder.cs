using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Tallyard.Api.Infrastructure;
using Tallyard.Api.Modules.Catalog;
using Tallyard.Api.Modules.Identity;
using Tallyard.Api.Modules.Inventory;
using Tallyard.Api.Modules.Partners;
using Tallyard.Api.Modules.Purchasing;
using Tallyard.Api.Modules.Sales;
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
        // Only creating the tenant needs the owner role (the app role can't insert tenants).
        // Everything else goes through the app role, so RLS keeps each tenant's data apart here
        // exactly as it does for requests.
        var current = new CurrentUser();
        Tenant tenant;
        await using (var owner = Db.Owner(config, current))
        {
            if (await owner.Tenants.AnyAsync(t => t.Slug == slug)) return;
            tenant = new Tenant { Slug = slug, Name = name, Currency = currency };
            owner.Tenants.Add(tenant);
            await owner.SaveChangesAsync();
        }
        current.Set(tenant.Id);
        await using var db = DbApp.Create(config, current);

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
        await OpeningStock(db, current, rng, whs);
        // Sales history first (back-dated), then purchasing, whose receipts happen now: stock
        // movements must be posted in date order for the stock card's running balance.
        await Sales(db, current, rng, whs, slug == "harborpine" ? 70 : 12);
        await Purchasing(db, current, rng, whs);
        Console.WriteLine($"Seeded {slug}: {whs.Count} warehouses, {made} products, {suppliers} suppliers, {customers} customers");
    }

    /// <summary>Opening balances through the real ledger (same code path as the app), one
    /// "opening" adjustment per warehouse. Some products are out of stock and some batches expire
    /// soon, so the screens have something to flag.</summary>
    static async Task OpeningStock(AppDbContext db, CurrentUser current, Random rng, List<Warehouse> warehouses)
    {
        var owner = await db.Users.FirstAsync(u => u.Role == Role.Owner);
        current.Set(current.RequiredTenantId, owner.Id, Role.Owner);
        var ledger = new StockLedger(db, current, new DocumentNumbers(db, current), new NoNotifier());
        var products = await db.Products.AsNoTracking().OrderBy(p => p.Sku).ToListAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        foreach (var w in warehouses)
        {
            var changes = new List<StockChange>();
            foreach (var p in products)
            {
                if (rng.NextDouble() < 0.08) continue; // out of stock here
                var cost = Math.Round(p.SalePrice * (decimal)(0.58 + rng.NextDouble() * 0.17), 4);
                if (!p.TrackBatches)
                {
                    changes.Add(new StockChange(p.Id, w.Id, rng.Next(6, 60) * 12, UnitCost: cost));
                    continue;
                }
                for (var i = 0; i < rng.Next(1, 4); i++)
                {
                    var expires = today.AddDays(rng.NextDouble() < 0.15 ? rng.Next(3, 25) : rng.Next(30, 200));
                    var batch = await ledger.BatchAsync(p.Id, $"L{expires:yyMMdd}-{rng.Next(10, 99)}", expires, default);
                    changes.Add(new StockChange(p.Id, w.Id, rng.Next(2, 30) * 6, batch.Id, cost));
                }
            }
            // Before the sales history begins, so the stock card's running balance reads in date order.
            await ledger.PostAsync(new PostRequest(OperationKind.Adjustment, "ADJ", changes, Reason: "opening",
                Note: "Opening balance", At: DateTimeOffset.UtcNow.AddDays(-130)), default);
        }
    }

    /// <summary>Approval thresholds, reorder rules, and purchase orders in every status, made by
    /// the people whose job it is (purchasing creates, finance and the owner approve, the
    /// warehouse receives), through the same service the API uses.</summary>
    static async Task Purchasing(AppDbContext db, CurrentUser current, Random rng, List<Warehouse> warehouses)
    {
        var tenant = current.RequiredTenantId;
        db.ApprovalRules.AddRange(
            new ApprovalRule { Level = 1, MinTotal = 2_000, Role = Role.Finance },
            new ApprovalRule { Level = 2, MinTotal = 10_000, Role = Role.Owner });
        await db.SaveChangesAsync();

        var users = await db.Users.ToDictionaryAsync(u => u.Role);
        var suppliers = await db.Suppliers.OrderBy(s => s.Code).ToListAsync();
        var categories = await db.Categories.ToDictionaryAsync(c => c.Id, c => c.Name);
        // Each category comes from one supplier (by name), falling back to the first.
        Supplier SupplierFor(Guid? categoryId)
        {
            var word = categoryId is { } c ? categories[c].Split(' ')[0] switch
            {
                "Beverages" => "Beverage", "Snacks" => "Snack", "Dairy" => "Dairy", "Pantry" => "Pantry",
                "Household" => "Household", _ => "Personal",
            } : "";
            return suppliers.FirstOrDefault(s => s.Name.Contains(word)) ?? suppliers[0];
        }

        var products = await db.Products.AsNoTracking().Include(p => p.Units).OrderBy(p => p.Sku).ToListAsync();
        foreach (var w in warehouses)
            foreach (var p in products.Where(_ => rng.NextDouble() < 0.7))
            {
                var pack = p.Units.Select(u => u.Factor).DefaultIfEmpty(1).Max();
                db.ReorderRules.Add(new ReorderRule
                {
                    ProductId = p.Id, WarehouseId = w.Id, SupplierId = SupplierFor(p.CategoryId).Id,
                    ReorderPoint = pack * rng.Next(4, 12), ReorderQuantity = pack * rng.Next(6, 16),
                });
            }
        await db.SaveChangesAsync();

        var numbers = new DocumentNumbers(db, current);
        var ledger = new StockLedger(db, current, numbers, new NoNotifier());
        PurchasingService As(Role role)
        {
            current.Set(tenant, users[role].Id, role);
            return new PurchasingService(db, current, numbers, ledger, new Quantities(db), new WarehouseAccess(db, current));
        }

        var costs = await db.StockBalances.AsNoTracking().GroupBy(b => b.ProductId)
            .Select(g => new { g.Key, Cost = g.Max(b => b.AvgCost) }).ToDictionaryAsync(x => x.Key, x => x.Cost);
        PoInput Order(Supplier supplier, Warehouse w, decimal targetTotal, int expectedInDays)
        {
            var pool = products.Where(p => SupplierFor(p.CategoryId).Id == supplier.Id && costs.ContainsKey(p.Id)).ToList();
            var lines = new List<PoLineInput>();
            decimal total = 0;
            foreach (var p in pool.OrderBy(_ => rng.Next()))
            {
                if (total >= targetTotal) break;
                var pack = p.Units.OrderByDescending(u => u.Factor).FirstOrDefault();
                var unitPrice = Math.Round(costs[p.Id] * (pack?.Factor ?? 1), 2);
                var quantity = Math.Max(1, Math.Min(40, Math.Ceiling((targetTotal - total) / unitPrice / 2)));
                lines.Add(new PoLineInput(p.Id, quantity, pack?.Name, unitPrice, null));
                total += quantity * unitPrice;
            }
            return new PoInput(supplier.Id, w.Id, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(expectedInDays), null, lines.ToArray(), null);
        }
        Supplier S(string word) => suppliers.FirstOrDefault(s => s.Name.Contains(word)) ?? suppliers[0];
        var (chi, dal, atl) = (warehouses[0], warehouses[Math.Min(1, warehouses.Count - 1)], warehouses[^1]);

        // 1. A draft still being put together.
        await As(Role.Purchasing).CreateAsync(Order(S("Snack"), chi, 900, 7), default);

        // 2. Waiting for finance (level 1).
        var p2 = await As(Role.Purchasing).CreateAsync(Order(S("Beverage"), dal, 4_200, 6), default);
        await As(Role.Purchasing).SubmitAsync(p2.Id, default);

        // 3. Finance approved; above 10,000 it also needs the owner (level 2).
        var p3 = await As(Role.Purchasing).CreateAsync(Order(S("Dairy"), chi, 14_500, 5), default);
        await As(Role.Purchasing).SubmitAsync(p3.Id, default);
        await As(Role.Finance).ApproveAsync(p3.Id, "Within the dairy budget for Q4", default);

        // 4. Approved and sent to the supplier, arriving in a few days.
        var p4 = await As(Role.Purchasing).CreateAsync(Order(S("Household"), atl, 1_400, 3), default);
        await As(Role.Purchasing).SubmitAsync(p4.Id, default);
        await As(Role.Purchasing).TransitionAsync(p4.Id, (p, now) => p.MarkSent(now), default);

        // 5. Partly delivered: the rest is back-ordered.
        var p5 = await As(Role.Purchasing).CreateAsync(Order(S("Pantry"), chi, 3_000, -2), default);
        await As(Role.Purchasing).SubmitAsync(p5.Id, default);
        await As(Role.Finance).ApproveAsync(p5.Id, null, default);
        await As(Role.Purchasing).TransitionAsync(p5.Id, (p, now) => p.MarkSent(now), default);
        await Receive(As(Role.Warehouse), p5.Id, 0.6m);

        // 6. Fully received.
        var p6 = await As(Role.Purchasing).CreateAsync(Order(S("Personal"), chi, 1_200, -6), default);
        await As(Role.Purchasing).SubmitAsync(p6.Id, default);
        await As(Role.Purchasing).TransitionAsync(p6.Id, (p, now) => p.MarkSent(now), default);
        await Receive(As(Role.Warehouse), p6.Id, 1m);

        current.Set(tenant, users[Role.Owner].Id, Role.Owner);
        db.ChangeTracker.Clear();

        async Task Receive(PurchasingService svc, Guid id, decimal share)
        {
            var po = await svc.Load(id, default);
            var lines = new List<ReceiveLineInput>();
            foreach (var l in po.Lines)
            {
                var q = Math.Max(1, Math.Round(l.Quantity * share));
                var p = products.First(x => x.Id == l.ProductId);
                var expires = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(rng.Next(60, 240));
                lines.Add(new ReceiveLineInput(l.Id, q, p.TrackBatches ? $"L{expires:yyMMdd}-{rng.Next(10, 99)}" : null, p.TrackBatches ? expires : null));
            }
            db.ChangeTracker.Clear();
            await svc.ReceiveAsync(id, new ReceiveInput(lines.ToArray(), "Delivered by supplier truck", null), default);
        }
    }

    /// <summary>
    /// About four months of selling, each order taken as far as its age suggests: older orders
    /// are shipped, invoiced and mostly paid; recent ones are partly shipped, reserved, on hold or
    /// still drafts. Some invoices are left unpaid past their due date so receivables have an age.
    /// </summary>
    static async Task Sales(AppDbContext db, CurrentUser current, Random rng, List<Warehouse> warehouses, int count)
    {
        var tenant = current.RequiredTenantId;
        var users = await db.Users.ToDictionaryAsync(u => u.Role);
        var customers = await db.Customers.OrderBy(c => c.Code).ToListAsync();
        var products = await db.Products.AsNoTracking().Include(p => p.Units).ToListAsync();
        var numbers = new DocumentNumbers(db, current);
        var ledger = new StockLedger(db, current, numbers, new NoNotifier());
        SalesService As(Role role)
        {
            current.Set(tenant, users[role].Id, role);
            db.ChangeTracker.Clear();
            return new SalesService(db, current, numbers, ledger, new Quantities(db), new WarehouseAccess(db, current));
        }
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        string[] methods = ["BankTransfer", "BankTransfer", "BankTransfer", "Card", "Check", "Cash"];

        // Spread over ~120 days, more of them recent; a quarter from this week, so there is work in
        // progress to look at. Oldest first, so stock movements are posted in date order.
        var ages = Enumerable.Range(0, count).Select(i => i % 4 == 0 ? rng.Next(0, 7) : (int)Math.Round(Math.Pow(rng.NextDouble(), 1.6) * 120))
            .OrderByDescending(a => a).ToList();
        var shipmentIndex = 0;
        foreach (var age in ages)
        {
            var customer = customers[rng.Next(customers.Count)];
            var warehouse = warehouses[rng.Next(warehouses.Count)];
            var stocked = await db.StockBalances.AsNoTracking()
                .Where(b => b.WarehouseId == warehouse.Id && b.OnHand - b.Reserved > 48).Select(b => b.ProductId).ToListAsync();
            if (stocked.Count < 6) continue;
            var lines = stocked.OrderBy(_ => rng.Next()).Take(rng.Next(2, 7)).Select(id =>
            {
                var p = products.First(x => x.Id == id);
                var pack = p.Units.OrderByDescending(u => u.Factor).FirstOrDefault();
                var useCase = pack is not null && rng.NextDouble() < 0.7;
                return new SoLineInput(id, useCase ? rng.Next(1, 4) : rng.Next(6, 30), useCase ? pack!.Name : p.BaseUnit, null,
                    rng.NextDouble() < 0.2 ? 5 : 0, null);
            }).ToArray();

            var sales = As(Role.Sales);
            var so = await sales.CreateAsync(new SoInput(customer.Id, warehouse.Id, today.AddDays(-age + 3),
                rng.NextDouble() < 0.5 ? $"PO-{rng.Next(1000, 9999)}" : null, null, lines, null), default);
            so.OrderDate = today.AddDays(-age);
            await db.SaveChangesAsync();
            if (age <= 1 && rng.NextDouble() < 0.5) continue; // still a draft

            so = await As(Role.Sales).ConfirmAsync(so.Id, allowBackorder: true, default);
            if (so.Status == SoStatus.OnHold)
            {
                if (age <= 3) continue; // waiting for finance
                so = await As(Role.Finance).ReleaseAsync(so.Id, customer.CreditLimit == 0 ? "Paid in advance" : "Long-standing customer, approved", default);
            }
            if (age <= 3) continue; // confirmed and reserved, not shipped yet

            // Ship: everything reserved for older orders, about half for recent ones.
            var share = age > 7 ? 1m : 0.5m;
            var ship = so.Lines.Where(l => l.ReservedBase > 0)
                .Select(l => new ShipLineInput(l.Id, Math.Max(1, Math.Floor(l.ReservedBase / l.Factor * share)))).ToArray();
            if (ship.Length == 0) continue;
            // The warehouse clerk works in the first warehouse only; elsewhere the owner ships.
            var shipper = warehouse.Id == warehouses[0].Id ? Role.Warehouse : Role.Owner;
            // Times rise with posting order, so two shipments on one day stay in order.
            var shippedAt = new DateTimeOffset(today.AddDays(-Math.Max(0, age - 2)).ToDateTime(new TimeOnly(8, 0).AddMinutes(5 * shipmentIndex++)), TimeSpan.Zero);
            var shipment = await As(shipper).ShipAsync(so.Id, new ShipInput(ship, rng.NextDouble() < 0.5 ? "Own fleet" : "Midwest Freight", null, null), default,
                shippedAt < DateTimeOffset.UtcNow ? shippedAt : DateTimeOffset.UtcNow);
            var invoice = await As(Role.Finance).InvoiceAsync(shipment.Id, today.AddDays(-Math.Max(0, age - 2)), default);

            // Older invoices are mostly paid; some are left overdue.
            var paidChance = age > 60 ? 0.85 : age > 30 ? 0.6 : 0.25;
            if (rng.NextDouble() < paidChance)
            {
                var full = rng.NextDouble() < 0.85;
                var amount = full ? invoice.Total : Math.Round(invoice.Total * 0.5m, 2);
                var paidOn = invoice.IssueDate.AddDays(rng.Next(3, Math.Max(4, customer.PaymentTermsDays + 10)));
                await As(Role.Finance).PayAsync(new PaymentInput(customer.Id, amount, paidOn < today ? paidOn : today,
                    methods[rng.Next(methods.Length)], $"REF{rng.Next(100000, 999999)}",
                    [new PaymentAllocationInput(invoice.Id, amount)]), default);
            }
        }
        current.Set(tenant, users[Role.Owner].Id, Role.Owner);
        db.ChangeTracker.Clear();
    }

    sealed class NoNotifier : IStockNotifier
    {
        public Task BalancesChangedAsync(Guid tenantId, IReadOnlyList<BalanceChanged> changes, CancellationToken ct) => Task.CompletedTask;
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
