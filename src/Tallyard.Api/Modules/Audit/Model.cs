namespace Tallyard.Api.Modules.Audit;

/// <summary>
/// One change to an audited record. The table is partitioned by month, the app role may only
/// INSERT and SELECT, and a trigger rejects UPDATE/DELETE even for the owner.
/// </summary>
public class AuditLog
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid TenantId { get; set; }
    public Guid? UserId { get; set; }
    public required string Action { get; set; }      // created | updated | deleted
    public required string EntityType { get; set; }  // Product, Warehouse, ...
    public Guid EntityId { get; set; }
    /// <summary>JSON: {"field": [before, after]} (only changed fields for updates).</summary>
    public required string Changes { get; set; }
}
