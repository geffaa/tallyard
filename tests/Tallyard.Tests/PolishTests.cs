using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;

namespace Tallyard.Tests;

[Collection(ApiCollection.Name)]
public class PolishTests(ApiFixture api)
{
    static async Task<Guid> Warehouse(HttpClient c, string code) =>
        (await c.GetFromJsonAsync<JsonElement>("/api/warehouses")).EnumerateArray().First(w => w.GetProperty("code").GetString() == code).GetProperty("id").GetGuid();

    static MultipartFormDataContent Xlsx(string[] header, params object?[][] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("data");
        for (var c = 0; c < header.Length; c++) ws.Cell(1, c + 1).Value = header[c];
        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < rows[r].Length; c++)
                ws.Cell(r + 2, c + 1).Value = rows[r][c] switch { null => Blank.Value, decimal d => d, int i => i, var o => o.ToString() };
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        var file = new ByteArrayContent(ms.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        return new MultipartFormDataContent { { file, "file", "import.xlsx" } };
    }

    [Fact]
    public async Task A_count_adjusts_by_counted_minus_expected_so_sales_during_the_count_are_kept()
    {
        var owner = await api.LoginAs("owner");
        var chi = await Warehouse(owner, "CHI");
        var sku = "CNT-" + Guid.NewGuid().ToString("N")[..6].ToUpper();
        var barcode = "29" + Random.Shared.NextInt64(10_000_000_000, 99_999_999_999).ToString();
        var product = (await (await owner.PostAsJsonAsync("/api/products", new
        {
            sku, name = "Count test " + sku, baseUnit = "pcs", trackBatches = false, taxRate = 0, salePrice = 1, isActive = true,
            units = new[] { new { name = "case", factor = 6 } }, barcodes = new[] { new { code = barcode, unit = "case" } },
        })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await owner.PostAsJsonAsync("/api/stock/adjustments", new { warehouseId = chi, reason = "found", lines = new[] { new { productId = product, quantity = 20m } } }))
            .EnsureSuccessStatusCode();

        var count = await (await owner.PostAsJsonAsync("/api/stock-counts", new { warehouseId = chi })).Content.ReadFromJsonAsync<JsonElement>();
        var id = count.GetProperty("id").GetGuid();

        // While counting, 5 pcs are sold (adjusted out here): 15 on hand.
        (await owner.PostAsJsonAsync("/api/stock/adjustments", new { warehouseId = chi, reason = "damaged", lines = new[] { new { productId = product, quantity = -5m } } }))
            .EnsureSuccessStatusCode();

        // Two people scan the same shelf at once: two cases each = 24 pcs, none lost.
        var staff = await api.LoginAs("warehouse");
        var scans = Enumerable.Range(0, 4).Select(i => (i % 2 == 0 ? owner : staff).PostAsJsonAsync($"/api/stock-counts/{id}/scan", new { code = barcode }));
        foreach (var r in await Task.WhenAll(scans)) r.EnsureSuccessStatusCode();
        var line = (await owner.GetFromJsonAsync<JsonElement>($"/api/stock-counts/{id}")).GetProperty("lines").EnumerateArray()
            .First(l => l.GetProperty("sku").GetString() == sku);
        Assert.Equal(24m, line.GetProperty("counted").GetDecimal());
        Assert.Equal(4m, line.GetProperty("variance").GetDecimal()); // counted 24, expected 20 at the start

        (await owner.PostAsync($"/api/stock-counts/{id}/post", null)).EnsureSuccessStatusCode();
        var stock = (await owner.GetFromJsonAsync<JsonElement>($"/api/stock/{product}")).GetProperty("warehouses").EnumerateArray()
            .First(w => w.GetProperty("warehouseId").GetGuid() == chi);
        Assert.Equal(19m, stock.GetProperty("onHand").GetDecimal()); // 15 + 4: the sale during the count is not undone

        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync($"/api/stock-counts/{id}/post", null)).StatusCode);
    }

    [Fact]
    public async Task An_import_with_errors_previews_them_by_row_and_changes_nothing()
    {
        var buyer = await api.LoginAs("purchasing");
        var sku = "IMP-" + Guid.NewGuid().ToString("N")[..6].ToUpper();
        var file = Xlsx(["sku", "name", "base_unit", "case_size", "sale_price"],
            [sku, "Imported lemonade", "can", 24, 1.2m],
            [sku, "Duplicate row", "can", null, null],
            ["IMP-BAD", "Bad price", "can", null, "abc"],
            [null, "No SKU", "can", null, null]);
        var res = await (await buyer.PostAsync("/api/import/products", file)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(res.GetProperty("committed").GetBoolean());
        var errors = res.GetProperty("errors").EnumerateArray().Select(e => (e.GetProperty("row").GetInt32(), e.GetProperty("column").GetString())).ToList();
        Assert.Contains((3, "sku"), errors);        // duplicate (row numbers as in Excel: header is row 1)
        Assert.Contains((4, "sale_price"), errors); // not a number
        Assert.Contains((5, "sku"), errors);        // required
        Assert.Equal(0, (await buyer.GetFromJsonAsync<JsonElement>($"/api/products?q={sku}")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_clean_import_commits_products_then_opening_stock_through_the_ledger()
    {
        var owner = await api.LoginAs("owner");
        var sku = "IMP-" + Guid.NewGuid().ToString("N")[..6].ToUpper();
        var products = await (await owner.PostAsync("/api/import/products?commit=true",
            Xlsx(["sku", "name", "base_unit", "case_size", "sale_price", "track_batches"], [sku, "Imported juice 1 L", "carton", 12, 2.5m, "no"])))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(products.GetProperty("committed").GetBoolean());
        Assert.Equal(1, products.GetProperty("created").GetInt32());

        var stock = await (await owner.PostAsync("/api/import/opening_stock?commit=true",
            Xlsx(["warehouse", "sku", "quantity", "unit", "unit_cost"], ["DAL", sku, 3, "case", 18m])))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(stock.GetProperty("committed").GetBoolean());

        var id = (await owner.GetFromJsonAsync<JsonElement>($"/api/products?q={sku}")).GetProperty("items")[0].GetProperty("id").GetGuid();
        var dal = await Warehouse(owner, "DAL");
        var bal = (await owner.GetFromJsonAsync<JsonElement>($"/api/stock/{id}")).GetProperty("warehouses").EnumerateArray()
            .First(w => w.GetProperty("warehouseId").GetGuid() == dal);
        Assert.Equal(36m, bal.GetProperty("onHand").GetDecimal());
        Assert.Equal(1.5m, bal.GetProperty("avgCost").GetDecimal()); // 18 per case of 12

        // Sales staff can't import products.
        var sales = await api.LoginAs("sales");
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.PostAsync("/api/import/products", Xlsx(["sku"], ["X"]))).StatusCode);
    }

    [Fact]
    public async Task The_dashboard_leaves_money_out_for_roles_that_cannot_see_it()
    {
        var owner = await api.LoginAs("owner");
        var keys = (await owner.GetFromJsonAsync<JsonElement>("/api/dashboard")).GetProperty("tiles").EnumerateArray().Select(t => t.GetProperty("key").GetString()).ToList();
        Assert.Contains("stock_value", keys);
        Assert.Contains("overdue_receivables", keys);

        var staff = await api.LoginAs("warehouse");
        var dash = await staff.GetFromJsonAsync<JsonElement>("/api/dashboard");
        var staffKeys = dash.GetProperty("tiles").EnumerateArray().Select(t => t.GetProperty("key").GetString()).ToList();
        Assert.DoesNotContain("stock_value", staffKeys);
        Assert.DoesNotContain("overdue_receivables", staffKeys);
        Assert.Equal("units", dash.GetProperty("valueKind").GetString());
    }
}
