using Microsoft.EntityFrameworkCore;
using Tallyard.Api.Common;
using Tallyard.Api.Modules.Identity;

namespace Tallyard.Api.Infrastructure;

/// <summary>Named permissions, each a set of roles. Endpoints require a permission, never a role.</summary>
public static class Perm
{
    public const string ManageUsers = nameof(ManageUsers);
    public const string ManageWarehouses = nameof(ManageWarehouses);
    public const string EditCatalog = nameof(EditCatalog);
    public const string EditSuppliers = nameof(EditSuppliers);
    public const string EditCustomers = nameof(EditCustomers);
    public const string ViewAudit = nameof(ViewAudit);
    public const string AdjustStock = nameof(AdjustStock);
    public const string TransferStock = nameof(TransferStock);
    /// <summary>Purchase costs and stock value. Warehouse and sales staff see quantities only.</summary>
    public const string ViewCosts = nameof(ViewCosts);
    public const string ViewPurchasing = nameof(ViewPurchasing);
    public const string EditPurchaseOrders = nameof(EditPurchaseOrders);
    /// <summary>May act on an approval step; which step is decided by the approval rules.</summary>
    public const string ApprovePurchaseOrders = nameof(ApprovePurchaseOrders);
    public const string ReceiveGoods = nameof(ReceiveGoods);
    public const string ViewSales = nameof(ViewSales);
    /// <summary>Selling prices and invoice amounts (warehouse staff ship by quantity only).</summary>
    public const string ViewSalesPrices = nameof(ViewSalesPrices);
    public const string EditSalesOrders = nameof(EditSalesOrders);
    public const string ReleaseCreditHolds = nameof(ReleaseCreditHolds);
    public const string ShipOrders = nameof(ShipOrders);
    public const string ManageReceivables = nameof(ManageReceivables);

    public static readonly Dictionary<string, Role[]> Roles = new()
    {
        [ManageUsers] = [Role.Owner],
        [ManageWarehouses] = [Role.Owner],
        [EditCatalog] = [Role.Owner, Role.Purchasing],
        [EditSuppliers] = [Role.Owner, Role.Purchasing],
        [EditCustomers] = [Role.Owner, Role.Sales, Role.Finance],
        [ViewAudit] = [Role.Owner],
        [AdjustStock] = [Role.Owner, Role.Warehouse],
        [TransferStock] = [Role.Owner, Role.Warehouse],
        [ViewCosts] = [Role.Owner, Role.Purchasing, Role.Finance],
        [ViewPurchasing] = [Role.Owner, Role.Purchasing, Role.Finance, Role.Warehouse],
        [EditPurchaseOrders] = [Role.Owner, Role.Purchasing],
        [ApprovePurchaseOrders] = [Role.Owner, Role.Finance, Role.Purchasing],
        [ReceiveGoods] = [Role.Owner, Role.Warehouse],
        [ViewSales] = [Role.Owner, Role.Sales, Role.Finance, Role.Warehouse],
        [ViewSalesPrices] = [Role.Owner, Role.Sales, Role.Finance],
        [EditSalesOrders] = [Role.Owner, Role.Sales],
        [ReleaseCreditHolds] = [Role.Owner, Role.Finance],
        [ShipOrders] = [Role.Owner, Role.Warehouse],
        [ManageReceivables] = [Role.Owner, Role.Finance],
    };

    public static bool Has(Role role, string perm) => Roles[perm].Contains(role);

    /// <summary>The permissions a role holds, sent to the client so it can hide what it can't use.
    /// The API checks them again on every request.</summary>
    public static string[] For(Role role) => Roles.Where(r => r.Value.Contains(role)).Select(r => r.Key).ToArray();
}

/// <summary>Which warehouses the current user may see. Owners and "all warehouses" users get null (no limit).</summary>
public sealed class WarehouseAccess(AppDbContext db, CurrentUser user)
{
    HashSet<Guid>? cached;
    bool loaded;

    public async Task<HashSet<Guid>?> AllowedAsync(CancellationToken ct)
    {
        if (loaded) return cached;
        var me = await db.Users.AsNoTracking().Include(u => u.Warehouses)
            .FirstAsync(u => u.Id == user.RequiredUserId, ct);
        cached = me.Role == Role.Owner || me.AllWarehouses ? null : me.Warehouses.Select(w => w.WarehouseId).ToHashSet();
        loaded = true;
        return cached;
    }

    public async Task EnsureAsync(Guid warehouseId, CancellationToken ct)
    {
        var allowed = await AllowedAsync(ct);
        if (allowed is not null && !allowed.Contains(warehouseId)) throw AppException.Forbidden();
    }
}
