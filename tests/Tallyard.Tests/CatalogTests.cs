using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tallyard.Tests;

[Collection(ApiCollection.Name)]
public class CatalogTests(ApiFixture api)
{
    static async Task<(Guid Id, JsonObject Product)> AnyProduct(HttpClient client, string q = "")
    {
        var page = await client.GetFromJsonAsync<JsonElement>($"/api/products?pageSize=1&q={q}");
        var id = page.GetProperty("items")[0].GetProperty("id").GetGuid();
        return (id, (await client.GetFromJsonAsync<JsonObject>($"/api/products/{id}"))!);
    }

    [Fact]
    public async Task Barcode_and_pack_size_changes_are_recorded_on_the_products_history()
    {
        var owner = await api.LoginAs("owner");
        var (id, p) = await AnyProduct(owner, "juice");
        var barcodes = p["barcodes"]!.AsArray();
        barcodes.Add(new JsonObject { ["code"] = "4006381333931", ["unit"] = p["baseUnit"]!.GetValue<string>() });
        p["units"]!.AsArray()[0]!["factor"] = 99;
        (await owner.PutAsJsonAsync($"/api/products/{id}", p)).EnsureSuccessStatusCode();

        var audit = await owner.GetFromJsonAsync<JsonElement>($"/api/audit?entityId={id}");
        var changes = audit.GetProperty("items")[0].GetProperty("changes");
        Assert.True(changes.TryGetProperty("barcode 4006381333931", out _));
        Assert.Equal(99, changes.GetProperty("unit case")[1].GetDecimal());
    }

    [Fact]
    public async Task A_barcode_can_belong_to_only_one_product()
    {
        var owner = await api.LoginAs("owner");
        var (_, a) = await AnyProduct(owner, "tea");
        var (idB, b) = await AnyProduct(owner, "rice");
        var taken = a["barcodes"]![0]!["code"]!.GetValue<string>();
        b["barcodes"]!.AsArray().Add(new JsonObject { ["code"] = taken, ["unit"] = null });

        var res = await owner.PutAsJsonAsync($"/api/products/{idB}", b);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("barcode_taken", body.GetProperty("code").GetString());
        Assert.Contains(a["sku"]!.GetValue<string>(), body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Saving_over_someone_elses_change_is_refused()
    {
        var owner = await api.LoginAs("owner");
        var (id, p) = await AnyProduct(owner, "soap");
        var stale = p.DeepClone().AsObject();

        p["salePrice"] = 4.5;
        (await owner.PutAsJsonAsync($"/api/products/{id}", p)).EnsureSuccessStatusCode();

        stale["salePrice"] = 5.5;
        var res = await owner.PutAsJsonAsync($"/api/products/{id}", stale);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("stale_record", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Search_finds_products_despite_a_typo_and_by_barcode()
    {
        var owner = await api.LoginAs("owner");
        var typo = await owner.GetFromJsonAsync<JsonElement>("/api/products?q=choclate");
        Assert.All(typo.GetProperty("items").EnumerateArray(), i => Assert.Contains("Chocolate", i.GetProperty("name").GetString()));
        Assert.True(typo.GetProperty("total").GetInt32() > 0);

        var (_, p) = await AnyProduct(owner, "yogurt");
        var code = p["barcodes"]![0]!["code"]!.GetValue<string>();
        var byCode = await owner.GetFromJsonAsync<JsonElement>($"/api/products?q={code}");
        Assert.Equal(p["sku"]!.GetValue<string>(), byCode.GetProperty("items")[0].GetProperty("sku").GetString());
    }

    [Fact]
    public async Task Validation_errors_name_the_field_and_follow_the_language()
    {
        var owner = await api.LoginAs("owner");
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/products")
        {
            Content = JsonContent.Create(new { sku = "", name = "", baseUnit = "pcs", units = new[] { new { name = "case", factor = 0 } } }),
        };
        req.Headers.AcceptLanguage.ParseAdd("id");
        var res = await owner.SendAsync(req);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Beberapa isian perlu diperbaiki", body.GetProperty("detail").GetString());
        var errors = body.GetProperty("errors");
        Assert.True(errors.TryGetProperty("sku", out _));
        Assert.True(errors.TryGetProperty("units[0].factor", out _));
    }
}
