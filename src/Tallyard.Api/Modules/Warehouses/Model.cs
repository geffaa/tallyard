using Tallyard.Api.Common;

namespace Tallyard.Api.Modules.Warehouses;

public class Warehouse : TenantEntity, IAudited
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public string? City { get; set; }
    public string? Address { get; set; }
    public bool IsActive { get; set; } = true;
    public uint Version { get; set; }
}
