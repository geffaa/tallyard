using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tallyard.Api.Common;
using Tallyard.Api.Infrastructure;

namespace Tallyard.Api.Modules.Audit;

public record AuditDto(Guid Id, DateTimeOffset CreatedAt, string? User, string Action, string EntityType, Guid EntityId, JsonElement Changes);

public record AuditQuery(int Page = 1, int PageSize = 50, string? EntityType = null, Guid? EntityId = null, Guid? UserId = null);

public static class AuditEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/audit", async ([AsParameters] AuditQuery p, AppDbContext db, CancellationToken ct) =>
        {
            var q = db.AuditLogs.AsNoTracking();
            if (p.EntityType is { } t) q = q.Where(x => x.EntityType == t);
            if (p.EntityId is { } id) q = q.Where(x => x.EntityId == id);
            if (p.UserId is { } u) q = q.Where(x => x.UserId == u);
            var page = await q.OrderByDescending(x => x.CreatedAt)
                .Select(x => new
                {
                    x.Id, x.CreatedAt, User = db.Users.Where(u => u.Id == x.UserId).Select(u => u.Name).FirstOrDefault(),
                    x.Action, x.EntityType, x.EntityId, x.Changes,
                })
                .ToPageAsync(new PageQuery(p.Page, p.PageSize), ct);
            return new Page<AuditDto>(
                page.Items.Select(x => new AuditDto(x.Id, x.CreatedAt, x.User, x.Action, x.EntityType, x.EntityId,
                    JsonDocument.Parse(x.Changes).RootElement)).ToList(),
                page.Total, page.PageNumber, page.PageSize);
        }).RequireAuthorization(Perm.ViewAudit);
    }
}
