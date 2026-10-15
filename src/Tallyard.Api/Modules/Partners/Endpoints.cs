using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Tallyard.Api.Common;
using Tallyard.Api.Infrastructure;

namespace Tallyard.Api.Modules.Partners;

public record PartnerDto(
    Guid Id, string Code, string Name, string? Email, string? Phone, string? Address, string? TaxId,
    int PaymentTermsDays, bool IsActive, int? LeadTimeDays, decimal? CreditLimit, uint Version);

public record PartnerInput(
    string Code, string Name, string? Email, string? Phone, string? Address, string? TaxId,
    int PaymentTermsDays, bool IsActive, int? LeadTimeDays, decimal? CreditLimit, uint? Version);

/// <summary>Suppliers and customers share fields and endpoints; each adds one field of its own.</summary>
public static class PartnerEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        MapPartner<Supplier>(api, "/suppliers", Perm.EditSuppliers, "supplier_not_found",
            (s, i) => s.LeadTimeDays = i.LeadTimeDays ?? 7,
            s => new PartnerDto(s.Id, s.Code, s.Name, s.Email, s.Phone, s.Address, s.TaxId, s.PaymentTermsDays, s.IsActive, s.LeadTimeDays, null, s.Version));

        MapPartner<Customer>(api, "/customers", Perm.EditCustomers, "customer_not_found",
            (c, i) => c.CreditLimit = i.CreditLimit ?? 0,
            c => new PartnerDto(c.Id, c.Code, c.Name, c.Email, c.Phone, c.Address, c.TaxId, c.PaymentTermsDays, c.IsActive, null, c.CreditLimit, c.Version));
    }

    static void MapPartner<T>(RouteGroupBuilder api, string path, string perm, string notFound,
        Action<T, PartnerInput> applyOwn, Func<T, PartnerDto> toDto) where T : Partner, new()
    {
        var g = api.MapGroup(path);

        g.MapGet("/", async ([AsParameters] PageQuery p, AppDbContext db, CancellationToken ct) =>
        {
            var q = db.Set<T>().AsNoTracking();
            if (!string.IsNullOrWhiteSpace(p.Q))
                q = q.Where(x => EF.Functions.ILike(x.Name, $"%{p.Q}%") || EF.Functions.ILike(x.Code, $"%{p.Q}%"));
            var page = await q.OrderBy(x => x.Name).ToPageAsync(p, ct);
            return new Page<PartnerDto>(page.Items.Select(toDto).ToList(), page.Total, page.PageNumber, page.PageSize);
        });

        g.MapGet("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
            toDto(await db.Set<T>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw AppException.NotFound(notFound)));

        g.MapPost("/", async (PartnerInput input, AppDbContext db, CancellationToken ct) =>
        {
            var code = input.Code.Trim().ToUpperInvariant();
            if (await db.Set<T>().AnyAsync(x => x.Code == code, ct)) throw AppException.Conflict("code_taken", ("code", code));
            var e = new T { Code = code, Name = input.Name.Trim() };
            Apply(e, input);
            applyOwn(e, input);
            db.Set<T>().Add(e);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api{path}/{e.Id}", toDto(e));
        }).RequireAuthorization(perm).Validated<PartnerInput>();

        g.MapPut("/{id:guid}", async (Guid id, PartnerInput input, AppDbContext db, CancellationToken ct) =>
        {
            var e = await db.Set<T>().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw AppException.NotFound(notFound);
            var code = input.Code.Trim().ToUpperInvariant();
            if (code != e.Code && await db.Set<T>().AnyAsync(x => x.Code == code, ct))
                throw AppException.Conflict("code_taken", ("code", code));
            db.Entry(e).Property(x => x.Version).OriginalValue = input.Version ?? e.Version;
            e.Code = code;
            e.Name = input.Name.Trim();
            Apply(e, input);
            applyOwn(e, input);
            await db.SaveChangesAsync(ct);
            return toDto(e);
        }).RequireAuthorization(perm).Validated<PartnerInput>();
    }

    static void Apply(Partner e, PartnerInput i)
    {
        e.Email = Blank(i.Email);
        e.Phone = Blank(i.Phone);
        e.Address = Blank(i.Address);
        e.TaxId = Blank(i.TaxId);
        e.PaymentTermsDays = i.PaymentTermsDays;
        e.IsActive = i.IsActive;
    }

    static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

public sealed class PartnerInputValidator : AbstractValidator<PartnerInput>
{
    public PartnerInputValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(20).Matches("^[A-Za-z0-9-]+$");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Email).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.PaymentTermsDays).InclusiveBetween(0, 365);
        RuleFor(x => x.LeadTimeDays).InclusiveBetween(0, 365);
        RuleFor(x => x.CreditLimit).GreaterThanOrEqualTo(0);
    }
}
