using System.Security.Claims;
using Tallyard.Api.Modules.Identity;

namespace Tallyard.Api.Infrastructure;

/// <summary>
/// Who is acting in this request. Filled from the auth cookie by middleware, or set
/// explicitly by login, background jobs and the seeder. The tenant id is what the database
/// connection uses for row-level security.
/// </summary>
public sealed class CurrentUser
{
    public Guid? TenantId { get; private set; }
    public Guid? UserId { get; private set; }
    public Role? Role { get; private set; }

    public Guid RequiredTenantId => TenantId ?? throw new InvalidOperationException("No tenant in scope");
    public Guid RequiredUserId => UserId ?? throw new InvalidOperationException("No user in scope");

    public void Set(Guid tenantId, Guid? userId = null, Role? role = null)
    {
        TenantId = tenantId;
        UserId = userId;
        Role = role;
    }

    public void SetFrom(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true) return;
        Set(
            Guid.Parse(principal.FindFirstValue(Claims.Tenant)!),
            Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!),
            Enum.Parse<Role>(principal.FindFirstValue(ClaimTypes.Role)!));
    }

    public static class Claims
    {
        public const string Tenant = "tenant";
    }
}
