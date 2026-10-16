using Microsoft.EntityFrameworkCore;
using Tallyard.Api.Common;
using Tallyard.Api.Modules.Audit;
using Tallyard.Api.Modules.Catalog;
using Tallyard.Api.Modules.Identity;
using Tallyard.Api.Modules.Inventory;
using Tallyard.Api.Modules.Partners;
using Tallyard.Api.Modules.Warehouses;

namespace Tallyard.Api.Infrastructure;

public class AppDbContext(DbContextOptions<AppDbContext> options, CurrentUser user) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserWarehouse> UserWarehouses => Set<UserWarehouse>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductUnit> ProductUnits => Set<ProductUnit>();
    public DbSet<ProductBarcode> ProductBarcodes => Set<ProductBarcode>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<DocumentSequence> DocumentSequences => Set<DocumentSequence>();
    public DbSet<Batch> Batches => Set<Batch>();
    public DbSet<StockBalance> StockBalances => Set<StockBalance>();
    public DbSet<BatchBalance> BatchBalances => Set<BatchBalance>();
    public DbSet<StockOperation> StockOperations => Set<StockOperation>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<Transfer> Transfers => Set<Transfer>();
    public DbSet<TransferLine> TransferLines => Set<TransferLine>();

    public override Task<int> SaveChangesAsync(bool acceptAll, CancellationToken ct = default)
    {
        StampTenant();
        return base.SaveChangesAsync(acceptAll, ct);
    }

    public override int SaveChanges(bool acceptAll)
    {
        StampTenant();
        return base.SaveChanges(acceptAll);
    }

    // New rows take the tenant in scope. RLS's WITH CHECK rejects a mismatch anyway; this just
    // means callers never have to set it.
    void StampTenant()
    {
        foreach (var entry in ChangeTracker.Entries<ITenantOwned>())
            if (entry.State == EntityState.Added && entry.Entity.TenantId == Guid.Empty)
                entry.Entity.TenantId = user.RequiredTenantId;
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresExtension("pg_trgm");

        // Ids are made by the app (UUIDv7), never by the database. Telling EF so matters: a new
        // child added to a loaded parent (a barcode on a product) is then inserted, not
        // mistaken for an existing row because its key is already set.
        foreach (var type in b.Model.GetEntityTypes().Where(t => typeof(Entity).IsAssignableFrom(t.ClrType)))
            b.Entity(type.ClrType).Property(nameof(Entity.Id)).ValueGeneratedNever();

        b.Entity<Tenant>(e =>
        {
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Slug).HasMaxLength(40);
            e.Property(x => x.Currency).HasMaxLength(3);
        });

        b.Entity<User>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Email }).IsUnique();
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
            e.HasMany(x => x.Warehouses).WithOne().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Version).IsRowVersion();
        });
        b.Entity<UserWarehouse>(e =>
        {
            e.HasKey(x => new { x.UserId, x.WarehouseId });
            e.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Warehouse>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            e.Property(x => x.Code).HasMaxLength(20);
            e.Property(x => x.Version).IsRowVersion();
        });

        b.Entity<Category>(e => e.HasIndex(x => new { x.TenantId, x.Name }).IsUnique());

        b.Entity<Product>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Sku }).IsUnique();
            // Fuzzy name search (and, later, matching supplier line items to SKUs).
            e.HasIndex(x => x.Name).HasMethod("gin").HasOperators("gin_trgm_ops");
            e.Property(x => x.Sku).HasMaxLength(40);
            e.Property(x => x.BaseUnit).HasMaxLength(20);
            e.Property(x => x.TaxRate).HasPrecision(5, 2);
            e.Property(x => x.SalePrice).HasPrecision(18, 2);
            e.HasMany(x => x.Units).WithOne().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Barcodes).WithOne().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.Version).IsRowVersion();
        });
        b.Entity<ProductUnit>(e =>
        {
            e.HasIndex(x => new { x.ProductId, x.Name }).IsUnique();
            e.Property(x => x.Factor).HasPrecision(18, 4);
            e.ToTable(t => t.HasCheckConstraint("ck_product_units_factor_positive", "factor > 0"));
        });
        b.Entity<ProductBarcode>(e => e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique());

        b.Entity<Supplier>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            e.Property(x => x.Version).IsRowVersion();
        });
        b.Entity<Customer>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            e.Property(x => x.CreditLimit).HasPrecision(18, 2);
            e.Property(x => x.Version).IsRowVersion();
            e.ToTable(t => t.HasCheckConstraint("ck_customers_credit_limit", "credit_limit >= 0"));
        });

        b.Entity<DocumentSequence>(e =>
        {
            e.HasKey(x => new { x.TenantId, x.Prefix, x.Year });
            e.Property(x => x.Prefix).HasMaxLength(10);
        });

        b.Entity<Batch>(e =>
        {
            e.HasIndex(x => new { x.ProductId, x.Number }).IsUnique();
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId);
            e.Property(x => x.Number).HasMaxLength(40);
        });

        b.Entity<StockBalance>(e =>
        {
            e.HasKey(x => new { x.ProductId, x.WarehouseId });
            e.HasIndex(x => new { x.TenantId, x.WarehouseId });
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId);
            e.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId);
            e.Property(x => x.OnHand).HasPrecision(18, 4);
            e.Property(x => x.Reserved).HasPrecision(18, 4);
            e.Property(x => x.AvgCost).HasPrecision(18, 4);
            e.Ignore(x => x.Available);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_stock_balances_on_hand", "on_hand >= 0");
                t.HasCheckConstraint("ck_stock_balances_reserved", "reserved >= 0 AND reserved <= on_hand");
                t.HasCheckConstraint("ck_stock_balances_avg_cost", "avg_cost >= 0");
            });
        });

        b.Entity<BatchBalance>(e =>
        {
            e.HasKey(x => new { x.WarehouseId, x.BatchId });
            e.HasIndex(x => new { x.ProductId, x.WarehouseId });
            e.HasOne<Batch>().WithMany().HasForeignKey(x => x.BatchId);
            e.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId);
            e.Property(x => x.OnHand).HasPrecision(18, 4);
            e.ToTable(t => t.HasCheckConstraint("ck_batch_balances_on_hand", "on_hand >= 0"));
        });

        b.Entity<StockOperation>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique().HasFilter("idempotency_key IS NOT NULL");
            e.HasIndex(x => new { x.TenantId, x.Number });
            e.HasIndex(x => new { x.SourceType, x.SourceId });
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Number).HasMaxLength(30);
            e.Property(x => x.IdempotencyKey).HasMaxLength(100);
            e.HasMany(x => x.Movements).WithOne().HasForeignKey(x => x.OperationId);
        });

        b.Entity<StockMovement>(e =>
        {
            // The stock card: one product in one warehouse, newest first.
            e.HasIndex(x => new { x.ProductId, x.WarehouseId, x.CreatedAt });
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId);
            e.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId);
            e.HasOne<Batch>().WithMany().HasForeignKey(x => x.BatchId);
            e.Property(x => x.Quantity).HasPrecision(18, 4);
            e.Property(x => x.UnitCost).HasPrecision(18, 4);
            e.Property(x => x.BalanceAfter).HasPrecision(18, 4);
            e.ToTable(t => t.HasCheckConstraint("ck_stock_movements_quantity", "quantity <> 0"));
        });

        b.Entity<Transfer>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.TransferId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.FromWarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.ToWarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Version).IsRowVersion();
            e.ToTable(t => t.HasCheckConstraint("ck_transfers_different_warehouses", "from_warehouse_id <> to_warehouse_id"));
        });
        b.Entity<TransferLine>(e =>
        {
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId);
            e.Property(x => x.Quantity).HasPrecision(18, 4);
            e.Property(x => x.UnitCost).HasPrecision(18, 4);
            e.ToTable(t => t.HasCheckConstraint("ck_transfer_lines_quantity", "quantity > 0"));
        });

        b.Entity<AuditLog>(e =>
        {
            // Partitioned by created_at, so the partition key is part of the primary key.
            e.HasKey(x => new { x.Id, x.CreatedAt });
            e.HasIndex(x => new { x.TenantId, x.EntityType, x.EntityId });
            e.HasIndex(x => new { x.TenantId, x.CreatedAt });
            e.Property(x => x.Changes).HasColumnType("jsonb");
        });
    }
}
