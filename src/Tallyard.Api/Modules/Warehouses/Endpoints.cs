using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Tallyard.Api.Common;
using Tallyard.Api.Infrastructure;

namespace Tallyard.Api.Modules.Warehouses;

public record WarehouseDto(Guid Id, string Code, string Name, string? City, string? Address, bool IsActive, uint Version);

public record WarehouseInput(string Code, string Name, string? City, string? Address, bool IsActive, uint? Version);

public static class WarehouseEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/warehouses");

        // Everyone signed in can list warehouses, but only the ones they have access to.
        g.MapGet("/", async (AppDbContext db, WarehouseAccess access, CancellationToken ct) =>
        {
            var allowed = await access.AllowedAsync(ct);
            var q = db.Warehouses.AsNoTracking();
            if (allowed is not null) q = q.Where(w => allowed.Contains(w.Id));
            return await q.OrderBy(w => w.Code).Select(w => ToDto(w)).ToListAsync(ct);
        });

        g.MapPost("/", async (WarehouseInput input, AppDbContext db, CancellationToken ct) =>
        {
            var code = input.Code.Trim().ToUpperInvariant();
            if (await db.Warehouses.AnyAsync(w => w.Code == code, ct))
                throw AppException.Conflict("warehouse_code_taken", ("code", code));
            var w = new Warehouse { Code = code, Name = input.Name.Trim() };
            Apply(w, input);
            db.Warehouses.Add(w);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/warehouses/{w.Id}", ToDto(w));
        }).RequireAuthorization(Perm.ManageWarehouses).Validated<WarehouseInput>();

        g.MapPut("/{id:guid}", async (Guid id, WarehouseInput input, AppDbContext db, CancellationToken ct) =>
        {
            var w = await db.Warehouses.FirstOrDefaultAsync(x => x.Id == id, ct)
                    ?? throw AppException.NotFound("warehouse_not_found");
            var code = input.Code.Trim().ToUpperInvariant();
            if (code != w.Code && await db.Warehouses.AnyAsync(x => x.Code == code, ct))
                throw AppException.Conflict("warehouse_code_taken", ("code", code));
            db.Entry(w).Property(x => x.Version).OriginalValue = input.Version ?? w.Version;
            w.Code = code;
            w.Name = input.Name.Trim();
            Apply(w, input);
            await db.SaveChangesAsync(ct);
            return ToDto(w);
        }).RequireAuthorization(Perm.ManageWarehouses).Validated<WarehouseInput>();
    }

    static void Apply(Warehouse w, WarehouseInput input)
    {
        w.City = input.City?.Trim();
        w.Address = input.Address?.Trim();
        w.IsActive = input.IsActive;
    }

    static WarehouseDto ToDto(Warehouse w) => new(w.Id, w.Code, w.Name, w.City, w.Address, w.IsActive, w.Version);
}

public sealed class WarehouseInputValidator : AbstractValidator<WarehouseInput>
{
    public WarehouseInputValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(20).Matches("^[A-Za-z0-9-]+$");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(120);
    }
}
