using Microsoft.EntityFrameworkCore;
using Tallyard.Api.Common;
using Tallyard.Api.Modules.Assistant;
using Tallyard.Api.Modules.Audit;
using Tallyard.Api.Modules.Catalog;
using Tallyard.Api.Modules.Identity;
using Tallyard.Api.Modules.Inventory;
using Tallyard.Api.Modules.Partners;
using Tallyard.Api.Modules.Purchasing;
using Tallyard.Api.Modules.Sales;
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
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<PurchaseOrderLine> PurchaseOrderLines => Set<PurchaseOrderLine>();
    public DbSet<ApprovalRule> ApprovalRules => Set<ApprovalRule>();
    public DbSet<PoApproval> PoApprovals => Set<PoApproval>();
    public DbSet<GoodsReceipt> GoodsReceipts => Set<GoodsReceipt>();
    public DbSet<GoodsReceiptLine> GoodsReceiptLines => Set<GoodsReceiptLine>();
    public DbSet<ReorderRule> ReorderRules => Set<ReorderRule>();
    public DbSet<SalesOrder> SalesOrders => Set<SalesOrder>();
    public DbSet<SalesOrderLine> SalesOrderLines => Set<SalesOrderLine>();
    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<ShipmentLine> ShipmentLines => Set<ShipmentLine>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentAllocation> PaymentAllocations => Set<PaymentAllocation>();
    public DbSet<SupplierDocument> SupplierDocuments => Set<SupplierDocument>();

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

        b.Entity<PurchaseOrder>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Status });
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Subtotal).HasPrecision(18, 2);
            e.Property(x => x.TaxTotal).HasPrecision(18, 2);
            e.Property(x => x.Total).HasPrecision(18, 2);
            e.HasOne<Supplier>().WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.PurchaseOrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Approvals).WithOne().HasForeignKey(x => x.PurchaseOrderId).OnDelete(DeleteBehavior.Cascade);
            e.Ignore(x => x.Editable);
            e.Ignore(x => x.Receivable);
            e.Property(x => x.Version).IsRowVersion();
        });
        b.Entity<PurchaseOrderLine>(e =>
        {
            e.HasIndex(x => x.ProductId);
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Unit).HasMaxLength(20);
            e.Property(x => x.Factor).HasPrecision(18, 4);
            e.Property(x => x.Quantity).HasPrecision(18, 4);
            e.Property(x => x.UnitPrice).HasPrecision(18, 4);
            e.Property(x => x.TaxRate).HasPrecision(5, 2);
            e.Property(x => x.Net).HasPrecision(18, 2);
            e.Property(x => x.Tax).HasPrecision(18, 2);
            e.Property(x => x.ReceivedBase).HasPrecision(18, 4);
            e.Ignore(x => x.OrderedBase);
            e.Ignore(x => x.RemainingBase);
            e.Ignore(x => x.BaseUnitCost);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_po_lines_quantity", "quantity > 0 AND factor > 0");
                t.HasCheckConstraint("ck_po_lines_price", "unit_price >= 0");
                // Never more than ordered plus the 5% tolerance (PurchasingService.OverReceiptTolerance).
                t.HasCheckConstraint("ck_po_lines_received", "received_base >= 0 AND received_base <= quantity * factor * 1.05");
            });
        });
        b.Entity<ApprovalRule>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Level }).IsUnique();
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.MinTotal).HasPrecision(18, 2);
        });
        b.Entity<PoApproval>(e =>
        {
            e.HasIndex(x => new { x.PurchaseOrderId, x.Level }).IsUnique();
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
        });
        b.Entity<GoodsReceipt>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
            e.HasIndex(x => x.PurchaseOrderId);
            e.HasOne<PurchaseOrder>().WithMany().HasForeignKey(x => x.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.GoodsReceiptId);
        });
        b.Entity<GoodsReceiptLine>(e =>
        {
            e.Property(x => x.QuantityBase).HasPrecision(18, 4);
            e.Property(x => x.UnitCost).HasPrecision(18, 4);
        });
        b.Entity<ReorderRule>(e =>
        {
            e.HasIndex(x => new { x.ProductId, x.WarehouseId }).IsUnique();
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Supplier>().WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.ReorderPoint).HasPrecision(18, 4);
            e.Property(x => x.ReorderQuantity).HasPrecision(18, 4);
        });

        b.Entity<SalesOrder>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
            e.HasIndex(x => new { x.CustomerId, x.Status });
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Subtotal).HasPrecision(18, 2);
            e.Property(x => x.TaxTotal).HasPrecision(18, 2);
            e.Property(x => x.Total).HasPrecision(18, 2);
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.SalesOrderId).OnDelete(DeleteBehavior.Cascade);
            e.Ignore(x => x.Editable);
            e.Ignore(x => x.Shippable);
            e.Property(x => x.Version).IsRowVersion();
        });
        b.Entity<SalesOrderLine>(e =>
        {
            e.HasIndex(x => x.ProductId);
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Unit).HasMaxLength(20);
            foreach (var p in new[] { nameof(SalesOrderLine.Factor), nameof(SalesOrderLine.Quantity), nameof(SalesOrderLine.UnitPrice),
                         nameof(SalesOrderLine.ReservedBase), nameof(SalesOrderLine.ShippedBase), nameof(SalesOrderLine.InvoicedBase) })
                e.Property(p).HasPrecision(18, 4);
            e.Property(x => x.DiscountPercent).HasPrecision(5, 2);
            e.Property(x => x.TaxRate).HasPrecision(5, 2);
            e.Property(x => x.Net).HasPrecision(18, 2);
            e.Property(x => x.Tax).HasPrecision(18, 2);
            e.Ignore(x => x.OrderedBase);
            e.Ignore(x => x.BackorderedBase);
            e.Ignore(x => x.BaseUnitNet);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_so_lines_quantity", "quantity > 0 AND factor > 0 AND unit_price >= 0");
                t.HasCheckConstraint("ck_so_lines_discount", "discount_percent BETWEEN 0 AND 100");
                // Reserved plus shipped never exceeds what was ordered; billed never exceeds shipped.
                t.HasCheckConstraint("ck_so_lines_fulfilment",
                    "reserved_base >= 0 AND shipped_base >= 0 AND reserved_base + shipped_base <= quantity * factor AND invoiced_base <= shipped_base");
            });
        });
        b.Entity<Shipment>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
            e.HasIndex(x => x.SalesOrderId);
            e.HasIndex(x => x.InvoiceId);
            e.HasOne<SalesOrder>().WithMany().HasForeignKey(x => x.SalesOrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.ShipmentId);
        });
        b.Entity<ShipmentLine>(e => e.Property(x => x.QuantityBase).HasPrecision(18, 4));
        b.Entity<Invoice>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
            e.HasIndex(x => new { x.CustomerId, x.Status, x.DueDate });
            // One invoice per shipment, enforced by the database as well.
            e.HasIndex(x => x.ShipmentId).IsUnique();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Subtotal).HasPrecision(18, 2);
            e.Property(x => x.TaxTotal).HasPrecision(18, 2);
            e.Property(x => x.Total).HasPrecision(18, 2);
            e.Property(x => x.AmountPaid).HasPrecision(18, 2);
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.InvoiceId);
            e.Ignore(x => x.Balance);
            e.Property(x => x.Version).IsRowVersion();
            e.ToTable(t => t.HasCheckConstraint("ck_invoices_paid", "amount_paid >= 0 AND amount_paid <= total"));
        });
        b.Entity<InvoiceLine>(e =>
        {
            e.Property(x => x.Unit).HasMaxLength(20);
            e.Property(x => x.Quantity).HasPrecision(18, 4);
            e.Property(x => x.UnitPrice).HasPrecision(18, 4);
            e.Property(x => x.DiscountPercent).HasPrecision(5, 2);
            e.Property(x => x.TaxRate).HasPrecision(5, 2);
            e.Property(x => x.Net).HasPrecision(18, 2);
            e.Property(x => x.Tax).HasPrecision(18, 2);
        });
        b.Entity<Payment>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
            e.HasIndex(x => x.CustomerId);
            e.Property(x => x.Method).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Allocations).WithOne().HasForeignKey(x => x.PaymentId);
            e.ToTable(t => t.HasCheckConstraint("ck_payments_amount", "amount > 0"));
        });
        b.Entity<PaymentAllocation>(e =>
        {
            e.HasIndex(x => x.InvoiceId);
            e.HasOne<Invoice>().WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.ToTable(t => t.HasCheckConstraint("ck_payment_allocations_amount", "amount > 0"));
        });

        b.Entity<SupplierDocument>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.CreatedAt });
            e.Property(x => x.FileName).HasMaxLength(255);
            e.Property(x => x.ContentType).HasMaxLength(60);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Result).HasColumnType("jsonb");
            e.HasOne<PurchaseOrder>().WithMany().HasForeignKey(x => x.PurchaseOrderId).OnDelete(DeleteBehavior.SetNull);
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
