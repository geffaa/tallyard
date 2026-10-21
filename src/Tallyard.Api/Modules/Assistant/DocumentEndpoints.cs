using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Tallyard.Api.Common;
using Tallyard.Api.Infrastructure;
using Tallyard.Api.Modules.Purchasing;

namespace Tallyard.Api.Modules.Assistant;

public record DocumentListItem(Guid Id, string FileName, string Status, string? Supplier, string? DocumentNumber, int Lines, int Flagged,
    DateTimeOffset CreatedAt, string? PurchaseOrder, Guid? PurchaseOrderId);

public record DocumentDto(Guid Id, string FileName, string ContentType, string Status, string? ErrorCode, string? ErrorMessage, string? Reader,
    DocumentResult? Result, int Tokens, int DurationMs, DateTimeOffset CreatedAt, Guid? PurchaseOrderId, string? PurchaseOrder);

public record ConvertLine(Guid ProductId, decimal Quantity, string Unit, decimal UnitPrice);
public record ConvertInput(Guid SupplierId, Guid WarehouseId, string? Note, ConvertLine[] Lines);

public static class DocumentEndpoints
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/ai/documents").RequireAuthorization(Perm.EditPurchaseOrders);

        g.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
        {
            var docs = await db.SupplierDocuments.AsNoTracking().OrderByDescending(d => d.CreatedAt).Take(100)
                .Select(d => new { d.Id, d.FileName, d.Status, d.Result, d.CreatedAt, d.PurchaseOrderId,
                    Po = db.PurchaseOrders.Where(p => p.Id == d.PurchaseOrderId).Select(p => p.Number).FirstOrDefault() })
                .ToListAsync(ct);
            return docs.Select(d =>
            {
                var r = d.Result is null ? null : JsonSerializer.Deserialize<DocumentResult>(d.Result, Json);
                return new DocumentListItem(d.Id, d.FileName, d.Status.ToString(), r?.Supplier?.Name ?? r?.Extracted.SupplierName,
                    r?.Extracted.DocumentNumber, r?.Lines.Length ?? 0, r?.Lines.Count(l => l.Flags.Length > 0) ?? 0, d.CreatedAt, d.Po, d.PurchaseOrderId);
            });
        });

        // Upload and read in one request: Groq answers in seconds, so the person waits on the
        // spinner instead of polling. A failure is stored too, so it shows up in the list.
        g.MapPost("/", async (IFormFile file, AppDbContext db, Extraction extraction, CurrentUser me, ILogger<Extraction> log, HttpContext http, CancellationToken ct) =>
        {
            if (file.Length == 0) throw new AppException(422, "file_empty");
            if (file.Length > Extraction.MaxBytes) throw new AppException(422, "file_too_large", ("mb", Extraction.MaxBytes / 1024 / 1024));
            var type = file.ContentType.ToLowerInvariant();
            if (!Extraction.Accepted.Contains(type)) throw new AppException(422, "unsupported_document");
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);

            var doc = new SupplierDocument { FileName = Path.GetFileName(file.FileName), ContentType = type, Content = ms.ToArray(), UploadedBy = me.UserId };
            try
            {
                var (result, reader, tokens, duration) = await extraction.RunAsync(doc.Content, type, ct);
                doc.Status = DocumentStatus.Ready;
                doc.Result = JsonSerializer.Serialize(result, Json);
                (doc.Reader, doc.Tokens, doc.DurationMs) = (reader, tokens, duration);
            }
            catch (AppException e)
            {
                doc.Status = DocumentStatus.Failed;
                doc.ErrorCode = e.Code;
            }
            catch (Exception e) when (e is JsonException or HttpRequestException or TaskCanceledException)
            {
                log.LogWarning(e, "Reading {File} failed", doc.FileName);
                doc.Status = DocumentStatus.Failed;
                doc.ErrorCode = "ai_unavailable";
            }
            db.SupplierDocuments.Add(doc);
            await db.SaveChangesAsync(ct);
            return Results.Ok(await Dto(db, doc.Id, Messages.LangOf(http), ct));
        }).DisableAntiforgery();

        g.MapGet("/{id:guid}", async (Guid id, AppDbContext db, HttpContext http, CancellationToken ct) => await Dto(db, id, Messages.LangOf(http), ct));

        g.MapGet("/{id:guid}/file", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var d = await db.SupplierDocuments.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Content, x.ContentType, x.FileName })
                        .FirstOrDefaultAsync(ct) ?? throw AppException.NotFound("document_not_found");
            return Results.File(d.Content, d.ContentType, enableRangeProcessing: true);
        });

        // The reviewed lines become a draft purchase order; the document links to it.
        g.MapPost("/{id:guid}/purchase-order", async (Guid id, ConvertInput input, AppDbContext db, PurchasingService purchasing, CancellationToken ct) =>
        {
            var doc = await db.SupplierDocuments.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw AppException.NotFound("document_not_found");
            if (doc.Status != DocumentStatus.Ready) throw AppException.Conflict("document_not_ready");
            var result = JsonSerializer.Deserialize<DocumentResult>(doc.Result!, Json)!;
            var note = input.Note ?? (result.Extracted.DocumentNumber is { } n ? $"From supplier document {n}" : null);
            var po = await purchasing.CreateAsync(new PoInput(input.SupplierId, input.WarehouseId, null, note,
                input.Lines.Select(l => new PoLineInput(l.ProductId, l.Quantity, l.Unit, l.UnitPrice, null)).ToArray(), null), ct);
            doc.PurchaseOrderId = po.Id;
            doc.Status = DocumentStatus.Converted;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { po.Id, po.Number });
        }).Validated<ConvertInput>();
    }

    static async Task<DocumentDto> Dto(AppDbContext db, Guid id, string lang, CancellationToken ct)
    {
        var d = await db.SupplierDocuments.AsNoTracking().Where(x => x.Id == id)
                    .Select(x => new { x.Id, x.FileName, x.ContentType, x.Status, x.ErrorCode, x.Reader, x.Result, x.Tokens, x.DurationMs, x.CreatedAt, x.PurchaseOrderId,
                        Po = db.PurchaseOrders.Where(p => p.Id == x.PurchaseOrderId).Select(p => p.Number).FirstOrDefault() })
                    .FirstOrDefaultAsync(ct) ?? throw AppException.NotFound("document_not_found");
        return new DocumentDto(d.Id, d.FileName, d.ContentType, d.Status.ToString(), d.ErrorCode,
            d.ErrorCode is null ? null : Messages.Render(d.ErrorCode, lang), d.Reader,
            d.Result is null ? null : JsonSerializer.Deserialize<DocumentResult>(d.Result, Json), d.Tokens, d.DurationMs, d.CreatedAt, d.PurchaseOrderId, d.Po);
    }
}

public sealed class ConvertInputValidator : AbstractValidator<ConvertInput>
{
    public ConvertInputValidator()
    {
        RuleFor(x => x.SupplierId).NotEmpty();
        RuleFor(x => x.WarehouseId).NotEmpty();
        RuleFor(x => x.Lines).NotEmpty();
        RuleForEach(x => x.Lines).ChildRules(l =>
        {
            l.RuleFor(x => x.ProductId).NotEmpty();
            l.RuleFor(x => x.Quantity).GreaterThan(0);
            l.RuleFor(x => x.UnitPrice).GreaterThanOrEqualTo(0);
        });
    }
}
