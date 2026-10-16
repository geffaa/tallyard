using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Tallyard.Api.Common;

public record PdfParty(string Name, string? Address, string? Email, string? Phone, string? TaxId);

/// <summary>A line; prices are null on documents that don't show them (a delivery note).</summary>
public record PdfLine(string Sku, string Name, decimal Quantity, string Unit, decimal? UnitPrice, decimal? TaxRate, decimal? Net, string? Detail = null);

public record PdfDoc(
    string Title, string Number, string Currency, PdfParty Issuer, string PartyLabel, PdfParty Party,
    string? ShipToLabel, string? ShipTo, IReadOnlyList<(string Label, string Value)> Fields, string? Note,
    IReadOnlyList<PdfLine> Lines, decimal? Subtotal, decimal? Tax, decimal? Total, string Footer,
    IReadOnlyList<(string Label, string Value)>? After = null);

/// <summary>
/// Business documents sent outside the company (purchase order, delivery note, invoice): A4, the
/// issuer at the top, the other party, key fields, one table, totals. One layout for all of them.
/// </summary>
public static class DocumentPdf
{
    static readonly string Ink = "#18222C", Muted = "#6B7480", Line = "#E3DFD5", Brand = "#0E5E5E";

    public static byte[] Render(PdfDoc d)
    {
        var prices = d.Lines.Any(l => l.UnitPrice is not null);
        string Money(decimal v) => v.ToString("N2") + " " + d.Currency;
        return Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(40);
            page.DefaultTextStyle(t => t.FontSize(9.5f).FontColor(Ink));

            page.Header().Column(col =>
            {
                col.Item().Row(row =>
                {
                    row.RelativeItem().Column(c =>
                    {
                        c.Item().Text(d.Issuer.Name).FontSize(14).Bold();
                        if (d.Issuer.Address is { } a) c.Item().Text(a).FontColor(Muted);
                    });
                    row.ConstantItem(220).AlignRight().Column(c =>
                    {
                        c.Item().AlignRight().Text(d.Title.ToUpperInvariant()).FontSize(10).Bold().FontColor(Brand).LetterSpacing(0.08f);
                        c.Item().AlignRight().Text(d.Number).FontSize(16).Bold();
                    });
                });
                col.Item().PaddingVertical(14).LineHorizontal(1).LineColor(Line);
            });

            page.Content().Column(col =>
            {
                col.Spacing(16);
                col.Item().Row(row =>
                {
                    row.RelativeItem().Element(Party(d.PartyLabel, d.Party));
                    if (d.ShipTo is { } shipTo)
                    {
                        row.ConstantItem(24);
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text((d.ShipToLabel ?? "Deliver to").ToUpperInvariant()).FontSize(8).Bold().FontColor(Muted);
                            c.Item().Text(shipTo);
                        });
                    }
                    row.ConstantItem(24);
                    row.ConstantItem(170).Column(c =>
                    {
                        foreach (var (label, value) in d.Fields) Field(c, label, value);
                    });
                });

                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(c =>
                    {
                        c.ConstantColumn(22);
                        c.ConstantColumn(88);
                        c.RelativeColumn();
                        c.ConstantColumn(72);
                        if (prices)
                        {
                            c.ConstantColumn(70);
                            c.ConstantColumn(38);
                            c.ConstantColumn(80);
                        }
                    });
                    t.Header(h =>
                    {
                        var heads = new List<(string, bool)> { ("#", false), ("SKU", false), ("Description", false), ("Qty", true) };
                        if (prices) heads.AddRange([("Unit price", true), ("Tax", true), ("Amount", true)]);
                        foreach (var (text, right) in heads)
                        {
                            var cell = h.Cell().BorderBottom(1).BorderColor(Ink).PaddingVertical(5);
                            (right ? cell.AlignRight() : cell).Text(text).FontSize(8).Bold().FontColor(Muted);
                        }
                    });
                    var i = 1;
                    foreach (var l in d.Lines)
                    {
                        IContainer Cell() => t.Cell().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(6);
                        Cell().Text($"{i++}").FontColor(Muted);
                        Cell().Text(l.Sku).FontSize(8.5f);
                        Cell().Column(c =>
                        {
                            c.Item().Text(l.Name);
                            if (l.Detail is { } detail) c.Item().Text(detail).FontSize(8).FontColor(Muted);
                        });
                        Cell().AlignRight().Text($"{l.Quantity:0.####} {l.Unit}");
                        if (prices)
                        {
                            Cell().AlignRight().Text(l.UnitPrice?.ToString("N2") ?? "");
                            Cell().AlignRight().Text(l.TaxRate is { } r ? $"{r:0.##}%" : "");
                            Cell().AlignRight().Text(l.Net?.ToString("N2") ?? "");
                        }
                    }
                });

                if (d.Total is { } total)
                    col.Item().AlignRight().Width(230).Column(c =>
                    {
                        Total(c, "Subtotal", Money(d.Subtotal ?? 0), false);
                        Total(c, "Tax", Money(d.Tax ?? 0), false);
                        c.Item().PaddingTop(4).LineHorizontal(1).LineColor(Ink);
                        Total(c, "Total", Money(total), true);
                        foreach (var (label, value) in d.After ?? []) Total(c, label, value, false);
                    });

                if (d.Note is { } note)
                    col.Item().Background("#F5F3EE").Padding(10).Column(c =>
                    {
                        c.Item().Text("NOTE").FontSize(8).Bold().FontColor(Muted);
                        c.Item().Text(note);
                    });
            });

            page.Footer().Row(row =>
            {
                row.RelativeItem().Text(d.Footer).FontColor(Muted).FontSize(8);
                row.ConstantItem(60).AlignRight().Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontSize(8).FontColor(Muted));
                    t.CurrentPageNumber();
                    t.Span(" / ");
                    t.TotalPages();
                });
            });
        })).GeneratePdf();
    }

    static Action<IContainer> Party(string title, PdfParty p) => c => c.Column(col =>
    {
        col.Item().Text(title.ToUpperInvariant()).FontSize(8).Bold().FontColor(Muted);
        col.Item().Text(p.Name).Bold();
        foreach (var line in new[] { p.Address, p.Email, p.Phone, p.TaxId is null ? null : "Tax ID " + p.TaxId })
            if (!string.IsNullOrWhiteSpace(line)) col.Item().Text(line);
    });

    static void Field(ColumnDescriptor c, string label, string value) => c.Item().Row(r =>
    {
        r.RelativeItem().Text(label).FontColor(Muted);
        r.AutoItem().Text(value);
    });

    static void Total(ColumnDescriptor c, string label, string value, bool strong) => c.Item().PaddingTop(3).Row(r =>
    {
        var l = r.RelativeItem().Text(label);
        var v = r.AutoItem().Text(value);
        if (strong) { l.Bold(); v.Bold().FontSize(11); }
        else l.FontColor(Muted);
    });
}
