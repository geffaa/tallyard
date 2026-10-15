using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Tallyard.Api.Common;
using Tallyard.Api.Infrastructure;

namespace Tallyard.Api.Modules.Identity;

public record UserDto(Guid Id, string Name, string Email, string Role, bool IsActive, bool AllWarehouses, Guid[] WarehouseIds, uint Version);

public record UserInput(string Name, string Email, string Role, bool IsActive, bool AllWarehouses, Guid[]? WarehouseIds, string? Password, uint? Version);

public static class UserEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/users").RequireAuthorization(Perm.ManageUsers);

        g.MapGet("/", async ([AsParameters] PageQuery p, AppDbContext db, CancellationToken ct) =>
        {
            var q = db.Users.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(p.Q))
                q = q.Where(u => EF.Functions.ILike(u.Name, $"%{p.Q}%") || EF.Functions.ILike(u.Email, $"%{p.Q}%"));
            return await q.OrderBy(u => u.Name)
                .Select(u => new UserDto(u.Id, u.Name, u.Email, u.Role.ToString(), u.IsActive, u.AllWarehouses,
                    u.Warehouses.Select(w => w.WarehouseId).ToArray(), u.Version))
                .ToPageAsync(p, ct);
        });

        g.MapPost("/", async (UserInput input, AppDbContext db, IPasswordHasher<User> hasher, CancellationToken ct) =>
        {
            var email = input.Email.Trim().ToLowerInvariant();
            if (await db.Users.AnyAsync(u => u.Email == email, ct)) throw AppException.Conflict("email_taken");
            var user = new User { Name = input.Name.Trim(), Email = email };
            Apply(user, input);
            await SetWarehouses(db, user, input.WarehouseIds ?? [], ct);
            user.PasswordHash = hasher.HashPassword(user, input.Password!);
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/users/{user.Id}", ToDto(user));
        }).Validated<UserInput>();

        g.MapPut("/{id:guid}", async (Guid id, UserInput input, AppDbContext db, IPasswordHasher<User> hasher, CancellationToken ct) =>
        {
            var user = await db.Users.Include(u => u.Warehouses).FirstOrDefaultAsync(u => u.Id == id, ct)
                       ?? throw AppException.NotFound("user_not_found");
            var email = input.Email.Trim().ToLowerInvariant();
            if (email != user.Email && await db.Users.AnyAsync(u => u.Email == email, ct))
                throw AppException.Conflict("email_taken");

            var wasActiveOwner = user is { Role: Role.Owner, IsActive: true };
            db.Entry(user).Property(u => u.Version).OriginalValue = input.Version ?? user.Version;
            user.Name = input.Name.Trim();
            user.Email = email;
            Apply(user, input);
            if (wasActiveOwner && user is not { Role: Role.Owner, IsActive: true } &&
                !await db.Users.AnyAsync(u => u.Id != id && u.Role == Role.Owner && u.IsActive, ct))
                throw AppException.Conflict("last_owner");

            await SetWarehouses(db, user, input.WarehouseIds ?? [], ct);
            if (!string.IsNullOrEmpty(input.Password)) user.PasswordHash = hasher.HashPassword(user, input.Password);
            await db.SaveChangesAsync(ct);
            return ToDto(user);
        }).Validated<UserInput>();
    }

    static void Apply(User user, UserInput input)
    {
        user.Role = Enum.Parse<Role>(input.Role);
        user.IsActive = input.IsActive;
        user.AllWarehouses = input.AllWarehouses;
    }

    static async Task SetWarehouses(AppDbContext db, User user, Guid[] ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToHashSet();
        var existing = await db.Warehouses.Where(w => wanted.Contains(w.Id)).Select(w => w.Id).ToListAsync(ct);
        if (existing.Count != wanted.Count) throw AppException.NotFound("warehouse_not_found");
        user.Warehouses.RemoveAll(w => !wanted.Contains(w.WarehouseId));
        foreach (var w in wanted.Where(w => user.Warehouses.All(x => x.WarehouseId != w)))
            user.Warehouses.Add(new UserWarehouse { UserId = user.Id, WarehouseId = w });
    }

    static UserDto ToDto(User u) => new(u.Id, u.Name, u.Email, u.Role.ToString(), u.IsActive, u.AllWarehouses,
        u.Warehouses.Select(w => w.WarehouseId).ToArray(), u.Version);
}

public sealed class UserInputValidator : AbstractValidator<UserInput>
{
    public UserInputValidator(IHttpContextAccessor http)
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(200);
        RuleFor(x => x.Role).IsEnumName(typeof(Role));
        // A new user needs a password; on update it is optional (empty keeps the current one).
        RuleFor(x => x.Password).NotEmpty()
            .When(_ => http.HttpContext?.Request.Method == "POST");
        RuleFor(x => x.Password).MinimumLength(8).When(x => !string.IsNullOrEmpty(x.Password));
    }
}
