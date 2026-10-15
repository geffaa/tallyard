using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Tallyard.Api.Common;
using Tallyard.Api.Modules.Audit;

namespace Tallyard.Api.Infrastructure;

/// <summary>
/// Before saving, records every change to an <see cref="IAudited"/> entity as an audit row in
/// the same transaction, so a change and its audit entry are committed together or not at all.
/// </summary>
public sealed class AuditInterceptor(CurrentUser user) : SaveChangesInterceptor
{
    static readonly HashSet<string> Ignored = ["UpdatedAt", "CreatedAt", "Version", "TenantId", "PasswordHash"];

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
    {
        if (data.Context is { } db) Record(db);
        return base.SavingChangesAsync(data, result, ct);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData data, InterceptionResult<int> result)
    {
        if (data.Context is { } db) Record(db);
        return base.SavingChanges(data, result);
    }

    void Record(DbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var logs = new List<AuditLog>();
        foreach (var entry in db.ChangeTracker.Entries().ToList())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            if (entry.Entity is TenantEntity t && entry.State == EntityState.Modified) t.UpdatedAt = now;
            if (entry.Entity is not IAudited || entry.Entity is not Entity e) continue;

            var changes = Changes(entry);
            if (entry.State == EntityState.Modified && changes.Count == 0) continue;
            logs.Add(new AuditLog
            {
                CreatedAt = now,
                TenantId = ((ITenantOwned)entry.Entity).TenantId,
                UserId = user.UserId,
                Action = entry.State switch
                {
                    EntityState.Added => "created",
                    EntityState.Deleted => "deleted",
                    _ => "updated",
                },
                EntityType = entry.Metadata.ClrType.Name,
                EntityId = e.Id,
                Changes = JsonSerializer.Serialize(changes),
            });
        }
        db.Set<AuditLog>().AddRange(logs);
    }

    static Dictionary<string, object?[]> Changes(EntityEntry entry)
    {
        var result = new Dictionary<string, object?[]>();
        foreach (var p in entry.Properties)
        {
            var name = p.Metadata.Name;
            if (Ignored.Contains(name) || name == "Id") continue;
            switch (entry.State)
            {
                case EntityState.Added when p.CurrentValue is not null:
                    result[Camel(name)] = [null, p.CurrentValue];
                    break;
                case EntityState.Deleted:
                    result[Camel(name)] = [p.OriginalValue, null];
                    break;
                case EntityState.Modified when p.IsModified && !Equals(p.OriginalValue, p.CurrentValue):
                    result[Camel(name)] = [p.OriginalValue, p.CurrentValue];
                    break;
            }
        }
        return result;
    }

    static string Camel(string s) => char.ToLowerInvariant(s[0]) + s[1..];
}
