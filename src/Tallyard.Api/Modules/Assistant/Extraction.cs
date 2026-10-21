using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Tallyard.Api.Common;
using Tallyard.Api.Infrastructure;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Tallyard.Api.Modules.Assistant;

public record ExtractedLine(
    string Description, string? NormalizedDescription, string? SupplierCode, decimal Quantity, string? Unit,
    decimal UnitPrice, decimal? LineTotal);

public record ExtractedDoc(
    string? SupplierName, string? DocumentNumber, string? DocumentDate, string? Currency,
    decimal? Subtotal, decimal? Charges, decimal? Tax, decimal? Total, ExtractedLine[] Lines);

public record Candidate(Guid ProductId, string Sku, string Name, double Score);

/// <summary>One extracted line, matched to a product, ready for a person to review.</summary>
public record ReviewLine(
    int Index, ExtractedLine Source, Guid? ProductId, string? Sku, string? Name, string Match, double Score,
    Candidate[] Candidates, string Unit, decimal Quantity, decimal UnitPrice, string[] Flags);

public record SupplierGuess(Guid Id, string Name, double Score);

public record DocumentResult(ExtractedDoc Extracted, SupplierGuess? Supplier, ReviewLine[] Lines, string[] Warnings, string Text);

/// <summary>
/// Turns a supplier's PDF or photo into reviewed-ready lines:
/// <list type="number">
/// <item>read the text (PDF text layer, or the vision model for photos);</item>
/// <item>extract a fixed JSON shape with the LLM (strict schema, temperature 0);</item>
/// <item>match the supplier and each line to a product: exact code or barcode, then trigram
/// similarity, then the LLM choosing among the closest candidates;</item>
/// <item>check the arithmetic and flag what a person should look at.</item>
/// </list>
/// The LLM never decides anything final: the flags come from deterministic checks, and the
/// result is a draft a person confirms.
/// </summary>
public sealed class Extraction(AppDbContext db, ILlm llm, AiOptions ai)
{
    public static readonly string[] Accepted = ["application/pdf", "image/png", "image/jpeg", "image/webp"];
    public const int MaxBytes = 10 * 1024 * 1024;

    public async Task<(DocumentResult Result, string Reader, int Tokens, int Ms)> RunAsync(byte[] file, string contentType, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var tokens = 0;
        string text, reader;
        if (contentType == "application/pdf")
        {
            text = PdfText(file);
            reader = "pdf-text";
            // A scan has no text layer; we don't rasterize PDFs, so ask for a photo instead.
            if (text.Count(char.IsLetterOrDigit) < 40) throw new AppException(422, "pdf_no_text");
        }
        else
        {
            var reply = await llm.ChatAsync(ai.VisionModel,
            [
                new("user", "Transcribe all text in this business document exactly as printed, keeping each table row on its own line " +
                            "and columns separated by ' | '. Output only the transcription.",
                    ImageDataUrl: $"data:{contentType};base64,{Convert.ToBase64String(file)}"),
            ], ct: ct);
            text = StripThinking(reply.Content ?? "");
            tokens += reply.Tokens;
            reader = "vision";
        }

        var (extracted, extractTokens) = await ExtractAsync(text, ct);
        tokens += extractTokens;
        var (result, matchTokens) = await MatchAsync(extracted, text, ct);
        tokens += matchTokens;
        return (result, reader, tokens, (int)clock.ElapsedMilliseconds);
    }

    static string PdfText(byte[] file)
    {
        try
        {
            using var pdf = PdfDocument.Open(file);
            var sb = new StringBuilder();
            foreach (var page in pdf.GetPages()) sb.AppendLine(ContentOrderTextExtractor.GetText(page));
            return sb.ToString();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Damaged, encrypted or not really a PDF.
            throw new AppException(422, "pdf_unreadable");
        }
    }

    // Some models wrap their reasoning in <think> tags before the answer.
    static string StripThinking(string s)
    {
        var end = s.LastIndexOf("</think>", StringComparison.Ordinal);
        return end >= 0 ? s[(end + 8)..].Trim() : s.Trim();
    }

    static readonly JsonObject Schema = (JsonObject)JsonNode.Parse("""
        {
          "type": "object", "additionalProperties": false,
          "required": ["supplierName", "documentNumber", "documentDate", "currency", "subtotal", "charges", "tax", "total", "lines"],
          "properties": {
            "supplierName": { "type": ["string", "null"] },
            "documentNumber": { "type": ["string", "null"] },
            "documentDate": { "type": ["string", "null"], "description": "YYYY-MM-DD" },
            "currency": { "type": ["string", "null"], "description": "ISO 4217 code" },
            "subtotal": { "type": ["number", "null"], "description": "Amount before tax, as printed" },
            "charges": { "type": ["number", "null"], "description": "Sum of freight, handling and other non-product rows; negative for discounts" },
            "tax": { "type": ["number", "null"] },
            "total": { "type": ["number", "null"] },
            "lines": {
              "type": "array",
              "items": {
                "type": "object", "additionalProperties": false,
                "required": ["description", "normalizedDescription", "supplierCode", "quantity", "unit", "unitPrice", "lineTotal"],
                "properties": {
                  "description": { "type": "string" },
                  "normalizedDescription": { "type": ["string", "null"] },
                  "supplierCode": { "type": ["string", "null"] },
                  "quantity": { "type": "number" },
                  "unit": { "type": ["string", "null"] },
                  "unitPrice": { "type": "number" },
                  "lineTotal": { "type": ["number", "null"] }
                }
              }
            }
          }
        }
        """)!;

    public async Task<(ExtractedDoc, int)> ExtractAsync(string text, CancellationToken ct)
    {
        // Suppliers abbreviate brand names ("BLCRST"); the model can only expand them if it knows
        // which brands we carry. The first word of a product name is the brand in this catalog.
        var brands = (await db.Products.AsNoTracking().Where(p => p.IsActive).Select(p => p.Name).ToListAsync(ct))
            .Select(n => n.Split(' ')[0]).Distinct().Order().Take(60).ToList();
        var reply = await llm.ChatAsync(ai.Model,
        [
            new("system", """
                You extract purchase documents (invoices, quotations, order confirmations) from suppliers into JSON.
                Rules:
                - Copy values as printed; never invent a value. Use null when a field is not on the document.
                - One entry in "lines" per product row. Freight, handling, deposits and discount rows are not lines:
                  add them up into "charges" (discounts negative).
                - quantity and unitPrice are numbers without currency symbols or thousands separators.
                  Read European formats correctly ("1.234,50" is 1234.5).
                - unit is the unit the quantity is counted in, as printed (e.g. "case", "CS", "ctn", "pcs", "bottle").
                - supplierCode is the item code, article number or barcode on the row, if any.
                - normalizedDescription spells out abbreviations in plain English with brand, product and size
                  (e.g. "BLCRST SPRK WTR 1L" -> "Bluecrest Sparkling Water 1 L").
                - documentDate as YYYY-MM-DD.
                """ + $"\nBrands we stock (use them to expand abbreviated brands): {string.Join(", ", brands)}."),
            new("user", text.Length > 20_000 ? text[..20_000] : text),
        ], Schema, ct: ct);
        var doc = JsonSerializer.Deserialize<ExtractedDoc>(reply.Content ?? "{}", new JsonSerializerOptions(JsonSerializerDefaults.Web))
                  ?? throw new AppException(502, "ai_unavailable");
        return (doc, reply.Tokens);
    }

    public async Task<(DocumentResult, int)> MatchAsync(ExtractedDoc doc, string text, CancellationToken ct)
    {
        var tokens = 0;
        var supplier = await MatchSupplier(doc.SupplierName, ct);

        // Products this supplier is known to deliver (reorder rules and past orders) rank higher:
        // a supplier's invoice is usually about the supplier's own range.
        var range = supplier is null ? [] : await db.ReorderRules.AsNoTracking().Where(r => r.SupplierId == supplier.Id).Select(r => r.ProductId)
            .Union(db.PurchaseOrderLines.AsNoTracking()
                .Where(l => db.PurchaseOrders.Any(p => p.Id == l.PurchaseOrderId && p.SupplierId == supplier.Id)).Select(l => l.ProductId))
            .Distinct().ToListAsync(ct);
        var inRange = range.ToHashSet();

        var lines = new List<ReviewLine>();
        var uncertain = new List<int>();
        for (var i = 0; i < doc.Lines.Length; i++)
        {
            var src = doc.Lines[i];
            var exact = await ByCode(src.SupplierCode, ct);
            var candidates = await Candidates(src.NormalizedDescription ?? src.Description, inRange, ct);
            if (exact is { } e)
            {
                lines.Add(Line(i, src, e.Product, e.Unit, e.Method, 1, candidates));
                continue;
            }
            var top = candidates.FirstOrDefault();
            var second = candidates.Skip(1).FirstOrDefault()?.Score ?? 0;
            if (top is not null && top.Score >= 0.75 && top.Score - second >= 0.12)
                lines.Add(Line(i, src, top, null, "name", top.Score, candidates));
            else
            {
                lines.Add(Line(i, src, null, null, "none", 0, candidates));
                if (candidates.Length > 0) uncertain.Add(i);
            }
        }

        // One call for all the lines the similarity score couldn't settle.
        if (uncertain.Count > 0)
        {
            var (picks, used) = await PickAsync(uncertain.Select(i => (i, doc.Lines[i], lines[i].Candidates)).ToList(), ct);
            tokens += used;
            foreach (var (i, sku) in picks)
            {
                var c = lines[i].Candidates.FirstOrDefault(x => x.Sku == sku);
                if (c is not null) lines[i] = Line(i, doc.Lines[i], c, null, "ai", c.Score, lines[i].Candidates);
            }
        }

        // Resolve units against each matched product, then run the checks.
        var products = await db.Products.AsNoTracking().Include(p => p.Units)
            .Where(p => lines.Select(l => l.ProductId).Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var reviewed = lines.Select(l =>
        {
            var flags = new List<string>();
            var unit = l.Unit;
            if (l.ProductId is { } pid && products.TryGetValue(pid, out var p))
            {
                var resolved = ResolveUnit(l.Source.Unit, p.BaseUnit, p.Units.Select(u => u.Name).ToList());
                if (string.IsNullOrEmpty(unit)) unit = resolved ?? p.BaseUnit;
                if (resolved is null && !string.IsNullOrWhiteSpace(l.Source.Unit)) flags.Add("unknown_unit");
            }
            if (l.ProductId is null) flags.Add("no_match");
            else if (l.Match == "ai" || l.Score < 0.75 && l.Match == "name") flags.Add("check_match");
            if (l.Source.LineTotal is { } total && Math.Abs(l.Source.Quantity * l.Source.UnitPrice - total) > Math.Max(0.02m, total * 0.005m))
                flags.Add("math");
            if (l.Source.Quantity <= 0) flags.Add("quantity");
            return l with { Unit = unit, Flags = flags.ToArray() };
        }).ToArray();

        var warnings = new List<string>();
        if (supplier is null) warnings.Add("supplier_unknown");
        var sum = doc.Lines.Sum(l => l.LineTotal ?? l.Quantity * l.UnitPrice) + (doc.Charges ?? 0);
        var expected = doc.Subtotal ?? (doc.Total is { } t ? t - (doc.Tax ?? 0) : null);
        if (expected is { } x && Math.Abs(sum - x) > Math.Max(0.05m, x * 0.005m)) warnings.Add("total_mismatch");
        if (doc.Lines.Length == 0) warnings.Add("no_lines");
        return (new DocumentResult(doc, supplier, reviewed, warnings.ToArray(), text), tokens);
    }

    static ReviewLine Line(int i, ExtractedLine src, Candidate? product, string? unit, string match, double score, Candidate[] candidates) =>
        new(i, src, product?.ProductId, product?.Sku, product?.Name, match, Math.Round(score, 3), candidates, unit ?? "", src.Quantity, src.UnitPrice, []);

    /// <summary>A code on the line that is one of our SKUs or barcodes; a case barcode also gives the unit.</summary>
    async Task<(Candidate Product, string? Unit, string Method)?> ByCode(string? code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var c = code.Trim();
        var bySku = await db.Products.AsNoTracking().Where(p => p.Sku == c.ToUpper()).Select(p => new { p.Id, p.Sku, p.Name }).FirstOrDefaultAsync(ct);
        if (bySku is not null) return (new Candidate(bySku.Id, bySku.Sku, bySku.Name, 1), null, "sku");
        var digits = new string(c.Where(char.IsDigit).ToArray());
        if (digits.Length < 8) return null;
        var byBarcode = await db.ProductBarcodes.AsNoTracking().Where(b => b.Code == digits)
            .Join(db.Products, b => b.ProductId, p => p.Id, (b, p) => new { p.Id, p.Sku, p.Name, b.Unit }).FirstOrDefaultAsync(ct);
        return byBarcode is null ? null : (new Candidate(byBarcode.Id, byBarcode.Sku, byBarcode.Name, 1), byBarcode.Unit, "barcode");
    }

    async Task<Candidate[]> Candidates(string description, HashSet<Guid> inRange, CancellationToken ct)
    {
        var rows = await db.Products.AsNoTracking().Where(p => p.IsActive)
            .Select(p => new { p.Id, p.Sku, p.Name, Score = EF.Functions.TrigramsWordSimilarity(description, p.Name) })
            .OrderByDescending(x => x.Score).Take(12).ToListAsync(ct);
        return rows.Select(r => new Candidate(r.Id, r.Sku, r.Name, Math.Min(1, r.Score + (inRange.Contains(r.Id) ? 0.05 : 0))))
            .OrderByDescending(c => c.Score).Take(6).ToArray();
    }

    static readonly JsonObject PickSchema = (JsonObject)JsonNode.Parse("""
        { "type": "object", "additionalProperties": false, "required": ["picks"],
          "properties": { "picks": { "type": "array", "items": {
            "type": "object", "additionalProperties": false, "required": ["line", "sku"],
            "properties": { "line": { "type": "integer" }, "sku": { "type": ["string", "null"] } } } } } }
        """)!;

    async Task<(List<(int Line, string? Sku)>, int)> PickAsync(List<(int Index, ExtractedLine Line, Candidate[] Candidates)> items, CancellationToken ct)
    {
        var sb = new StringBuilder();
        foreach (var (i, l, cs) in items)
        {
            sb.AppendLine($"Line {i}: \"{l.Description}\" ({l.NormalizedDescription}), unit {l.Unit}, price {l.UnitPrice}");
            foreach (var c in cs) sb.AppendLine($"  - {c.Sku}: {c.Name}");
        }
        var reply = await llm.ChatAsync(ai.Model,
        [
            new("system", "For each supplier invoice line, choose the one candidate product that is the same item (same brand, product and size). " +
                          "Answer null when none of the candidates is the same item. Sizes must match exactly."),
            new("user", sb.ToString()),
        ], PickSchema, ct: ct);
        var picks = JsonNode.Parse(reply.Content ?? "{}")?["picks"]?.AsArray()
            .Select(p => (p!["line"]!.GetValue<int>(), p["sku"]?.GetValue<string>())).ToList() ?? [];
        return (picks, reply.Tokens);
    }

    async Task<SupplierGuess?> MatchSupplier(string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var best = await db.Suppliers.AsNoTracking().Where(s => s.IsActive)
            .Select(s => new { s.Id, s.Name, Score = EF.Functions.TrigramsSimilarity(name, s.Name) })
            .OrderByDescending(s => s.Score).FirstOrDefaultAsync(ct);
        return best is { Score: >= 0.45 } ? new SupplierGuess(best.Id, best.Name, Math.Round(best.Score, 3)) : null;
    }

    static readonly Dictionary<string, string> PackWords = new()
    {
        ["case"] = "case", ["cases"] = "case", ["cs"] = "case", ["cse"] = "case", ["ctn"] = "case", ["ctns"] = "case",
        ["carton"] = "case", ["cartons"] = "case", ["box"] = "case", ["boxes"] = "case", ["pack"] = "case", ["tray"] = "case",
    };
    static readonly HashSet<string> EachWords = ["pc", "pcs", "piece", "pieces", "ea", "each", "unit", "units", "u"];

    /// <summary>The supplier's unit as one of the product's units, or null when it can't be told.</summary>
    public static string? ResolveUnit(string? printed, string baseUnit, IReadOnlyList<string> units)
    {
        if (string.IsNullOrWhiteSpace(printed)) return baseUnit;
        var u = printed.Trim().TrimEnd('.').ToLowerInvariant();
        if (u == baseUnit || u == baseUnit + "s") return baseUnit;
        if (units.Contains(u)) return u;
        // "carton" is a pack word for most products but the base unit for juice and milk.
        if (PackWords.TryGetValue(u, out var pack) && units.Contains(pack) && baseUnit != u) return pack;
        if (EachWords.Contains(u)) return baseUnit;
        return null;
    }
}
