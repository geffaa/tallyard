namespace Tallyard.Api.Common;

/// <summary>Ids are UUIDv7: generated in the app (known before insert, so audit rows can
/// reference them) and time-ordered, so B-tree inserts stay sequential.</summary>
public abstract class Entity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
}

/// <summary>Rows that belong to one tenant. Postgres row-level security filters them by
/// <c>app.tenant_id</c>; <see cref="Infrastructure.AppDbContext"/> fills the column on insert.</summary>
public interface ITenantOwned
{
    Guid TenantId { get; set; }
}

/// <summary>Changes to these entities are written to the append-only audit log.</summary>
public interface IAudited;

/// <summary>A child row (a product's pack size, a barcode) whose changes are logged on its
/// parent's audit entry, as one field: <see cref="AuditField"/> = <see cref="AuditValue"/>.</summary>
public interface IAuditedChild
{
    Guid AuditParentId { get; }
    string AuditParentType { get; }
    string AuditField { get; }
    object? AuditValue { get; }
}

public abstract class TenantEntity : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
