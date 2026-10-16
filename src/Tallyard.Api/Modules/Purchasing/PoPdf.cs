using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Tallyard.Api.Modules.Purchasing;

public record PdfParty(string Name, string? Address, string? Email, string? Phone, string? TaxId);
public record PdfLine(string Sku, string Name, decimal Quantity, string Unit, decimal UnitPrice, decimal TaxRate, decimal Net);
public record PdfOrder(
    string Number, DateOnly OrderDate, DateOnly? ExpectedOn, string Currency, PdfParty Buyer, PdfParty Supplier,
    string DeliverTo, string? Note, IReadOnlyList<PdfLine> Lines, decimal Subtotal, decimal Tax, decimal Total, int PaymentTermsDays);

/// <summary>The purchase order as sent to the supplier: A4, one table, totals, delivery address.</summary>
public static class PoPdf
{
    static readonly string Ink = "#18222C", Muted = "#6B7480", Line = "#E3DFD5", Brand = "#0E5E5E";

    public static byte[] Render(PdfOrder o)
    {
        var money = (decimal v) => v.ToString("N2") + " " + o.Currency;
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
                        c.Item().Text(o.Buyer.Name).FontSize(14).Bold();
                        if (o.Buyer.Address is { } a) c.Item().Text(a).FontColor(Muted);
                    });
                    row.ConstantItem(200).AlignRight().Column(c =>
                    {
                        c.Item().AlignRight().Text("PURCHASE ORDER").FontSize(10).Bold().FontColor(Brand).LetterSpacing(0.08f);
                        c.Item().AlignRight().Text(o.Number).FontSize(16).Bold();
                    });
                });
                col.Item().PaddingVertical(14).LineHorizontal(1).LineColor(Line);
            });

            page.Content().Column(col =>
            {
                col.Spacing(16);
                col.Item().Row(row =>
                {
                    row.RelativeItem().Element(Party("Supplier", o.Supplier));
                    row.ConstantItem(24);
                    row.RelativeItem().Column(c =>
                    {
                        c.Item().Text("DELIVER TO").FontSize(8).Bold().FontColor(Muted);
                        c.Item().Text(o.DeliverTo);
                    });
                    row.ConstantItem(24);
                    row.ConstantItem(150).Column(c =>
                    {
                        Field(c, "Order date", o.OrderDate.ToString("yyyy-MM-dd"));
                        if (o.ExpectedOn is { } e) Field(c, "Expected", e.ToString("yyyy-MM-dd"));
                        Field(c, "Payment terms", $"{o.PaymentTermsDays} days");
                    });
                });

                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(c =>
                    {
                        c.ConstantColumn(22);
                        c.ConstantColumn(90);
                        c.RelativeColumn();
                        c.ConstantColumn(70);
                        c.ConstantColumn(70);
                        c.ConstantColumn(38);
                        c.ConstantColumn(80);
                    });
                    t.Header(h =>
                    {
                        foreach (var (text, right) in new[] { ("#", false), ("SKU", false), ("Description", false), ("Qty", true), ("Unit price", true), ("Tax", true), ("Amount", true) })
                        {
                            var cell = h.Cell().BorderBottom(1).BorderColor(Ink).PaddingVertical(5);
                            (right ? cell.AlignRight() : cell).Text(text).FontSize(8).Bold().FontColor(Muted);
                        }
                    });
                    var i = 1;
                    foreach (var l in o.Lines)
                    {
                        IContainer Cell() => t.Cell().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(6);
                        Cell().Text($"{i++}").FontColor(Muted);
                        Cell().Text(l.Sku).FontSize(8.5f);
                        Cell().Text(l.Name);
                        Cell().AlignRight().Text($"{l.Quantity:0.####} {l.Unit}");
                        Cell().AlignRight().Text(l.UnitPrice.ToString("N2"));
                        Cell().AlignRight().Text($"{l.TaxRate:0.##}%");
                        Cell().AlignRight().Text(l.Net.ToString("N2"));
                    }
                });

                col.Item().AlignRight().Width(220).Column(c =>
                {
                    Total(c, "Subtotal", money(o.Subtotal), false);
                    Total(c, "Tax", money(o.Tax), false);
                    c.Item().PaddingTop(4).LineHorizontal(1).LineColor(Ink);
                    Total(c, "Total", money(o.Total), true);
                });

                if (o.Note is { } note)
                    col.Item().Background("#F5F3EE").Padding(10).Column(c =>
                    {
                        c.Item().Text("NOTE").FontSize(8).Bold().FontColor(Muted);
                        c.Item().Text(note);
                    });
            });

            page.Footer().Row(row =>
            {
                row.RelativeItem().Text($"Please quote {o.Number} on your invoice and delivery note.").FontColor(Muted).FontSize(8);
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
