using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Tallyard.Api.Common;
using Tallyard.Api.Infrastructure;

namespace Tallyard.Api.Modules.Identity;

public record LoginRequest(string Workspace, string Email, string Password);

public record MeResponse(
    Guid Id, string Name, string Email, string Role, string[] Permissions,
    TenantInfo Tenant, bool AllWarehouses, Guid[] WarehouseIds);

public record TenantInfo(Guid Id, string Slug, string Name, string Currency);

public static class AuthEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/auth");

        g.MapPost("/login", async (LoginRequest req, AppDbContext db, CurrentUser current,
            IPasswordHasher<User> hasher, HttpContext http, CancellationToken ct) =>
        {
            // Tenants are not RLS-protected: the workspace slug is how we find the tenant to scope to.
            var tenant = await db.Tenants.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Slug == req.Workspace.Trim().ToLower(), ct);
            if (tenant is null) throw new AppException(401, "invalid_login");

            current.Set(tenant.Id);
            var email = req.Email.Trim().ToLowerInvariant();
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email && u.IsActive, ct);
            var check = user is null
                ? PasswordVerificationResult.Failed
                : hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password);
            if (user is null || check == PasswordVerificationResult.Failed)
                throw new AppException(401, "invalid_login");

            if (check == PasswordVerificationResult.SuccessRehashNeeded)
            {
                user.PasswordHash = hasher.HashPassword(user, req.Password);
                await db.SaveChangesAsync(ct);
            }

            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, Principal(user), new AuthenticationProperties
            {
                IsPersistent = true,
            });
            current.Set(tenant.Id, user.Id, user.Role);
            return await Me(db, current, ct);
        }).AllowAnonymous().RequireRateLimiting("login");

        g.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        g.MapGet("/me", (AppDbContext db, CurrentUser current, CancellationToken ct) => Me(db, current, ct));
    }

    /// <summary>The cookie carries user, tenant and role; it is re-checked against the database on every request (see <see cref="SessionValidator"/>), so
    /// deactivating a user or changing their role takes effect immediately.</summary>
    public static ClaimsPrincipal Principal(User user) => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(CurrentUser.Claims.Tenant, user.TenantId.ToString()),
        new Claim(ClaimTypes.Role, user.Role.ToString()),
        new Claim(ClaimTypes.Name, user.Name),
    ], CookieAuthenticationDefaults.AuthenticationScheme));

    static async Task<MeResponse> Me(AppDbContext db, CurrentUser current, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Include(u => u.Warehouses)
            .FirstAsync(u => u.Id == current.RequiredUserId, ct);
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == user.TenantId, ct);
        return new MeResponse(
            user.Id, user.Name, user.Email, user.Role.ToString(), Perm.For(user.Role),
            new TenantInfo(tenant.Id, tenant.Slug, tenant.Name, tenant.Currency),
            user.Role == Role.Owner || user.AllWarehouses,
            user.Warehouses.Select(w => w.WarehouseId).ToArray());
    }
}

public sealed class LoginValidator : AbstractValidator<LoginRequest>
{
    public LoginValidator()
    {
        RuleFor(x => x.Workspace).NotEmpty();
        RuleFor(x => x.Email).NotEmpty();
        RuleFor(x => x.Password).NotEmpty();
    }
}

/// <summary>Rejects a cookie whose user was deactivated or whose role changed since sign-in.</summary>
public static class SessionValidator
{
    public static async Task Validate(CookieValidatePrincipalContext ctx)
    {
        var current = ctx.HttpContext.RequestServices.GetRequiredService<CurrentUser>();
        current.SetFrom(ctx.Principal!);
        var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == current.UserId)
            .Select(u => new { u.IsActive, u.Role })
            .FirstOrDefaultAsync();
        if (user is null || !user.IsActive || user.Role != current.Role)
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }
}
