using Tallyard.Api.Common;

namespace Tallyard.Api.Modules.Identity;

public class Tenant : Entity
{
    public required string Slug { get; set; }
    public required string Name { get; set; }
    public string Currency { get; set; } = "USD";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum Role { Owner, Purchasing, Warehouse, Sales, Finance }

public class User : TenantEntity, IAudited
{
    public required string Email { get; set; }
    public required string Name { get; set; }
    public string PasswordHash { get; set; } = "";
    public Role Role { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Sees every warehouse; otherwise only those in <see cref="Warehouses"/>.
    /// Owners always see all of them.</summary>
    public bool AllWarehouses { get; set; }
    public List<UserWarehouse> Warehouses { get; set; } = [];
    public uint Version { get; set; }
}

public class UserWarehouse : ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid WarehouseId { get; set; }
}
