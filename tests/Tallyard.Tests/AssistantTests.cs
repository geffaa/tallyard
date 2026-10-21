using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Tallyard.Api.Modules.Assistant;

namespace Tallyard.Tests;

/// <summary>
/// The AI layer without the AI: a scripted model stands in for Groq, so these tests check what
/// the application does with model output (matching, flags, permissions), deterministically.
/// The real model's accuracy is measured separately by the evaluation set (eval/supplier-docs).
/// </summary>
[Collection(ApiCollection.Name)]
public class AssistantTests(ApiFixture api)
{
    sealed class ScriptedLlm : ILlm
    {
        public Func<string, IReadOnlyList<LlmMessage>, JsonObject?, JsonArray?, LlmReply> Reply = (_, _, _, _) => new("", [], 0);
        public Task<LlmReply> ChatAsync(string model, IReadOnlyList<LlmMessage> messages, JsonObject? jsonSchema = null,
            JsonArray? tools = null, CancellationToken ct = default) => Task.FromResult(Reply(model, messages, jsonSchema, tools));
    }

    async Task<(HttpClient Client, ScriptedLlm Llm)> As(string user)
    {
        var llm = new ScriptedLlm();
        var factory = api.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<ILlm>(llm)));
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        (await client.PostAsJsonAsync("/api/auth/login", new { workspace = "harborpine", email = $"{user}@harborpine.test", password = "tallyard123" }))
            .EnsureSuccessStatusCode();
        return (client, llm);
    }

    static MultipartFormDataContent Upload(string name = "invoice.png", string type = "image/png")
    {
        var file = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        return new MultipartFormDataContent { { file, "file", name } };
    }

    [Fact]
    public async Task Supplier_lines_are_matched_by_barcode_and_name_and_doubtful_ones_are_flagged()
    {
        var (client, llm) = await As("purchasing");
        var product = (await client.GetFromJsonAsync<JsonElement>("/api/products?q=greek yogurt&pageSize=1")).GetProperty("items")[0];
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/products/{product.GetProperty("id")}");
        var caseBarcode = detail.GetProperty("barcodes").EnumerateArray().First(b => b.GetProperty("unit").GetString() == "case").GetProperty("code").GetString();
        var supplier = (await client.GetFromJsonAsync<JsonElement>("/api/suppliers?q=Coastal")).GetProperty("items")[0].GetProperty("name").GetString();

        llm.Reply = (model, messages, schema, _) =>
        {
            if (messages[0].ImageDataUrl is not null) return new("transcribed text of the invoice", [], 10);
            if (schema?["properties"]?["lines"] is not null)
                return new(JsonSerializer.Serialize(new
                {
                    supplierName = supplier, documentNumber = "INV-1", documentDate = "2026-10-01", currency = "USD",
                    subtotal = 150m, charges = (decimal?)null, tax = (decimal?)null, total = 150m,
                    lines = new object[]
                    {
                        new { description = "yogurt", normalizedDescription = (string?)null, supplierCode = caseBarcode, quantity = 2m, unit = "CS", unitPrice = 50m, lineTotal = 100m },
                        new { description = "Mystery item", normalizedDescription = "Unobtainium Widget", supplierCode = (string?)null, quantity = 1m, unit = "pc", unitPrice = 40m, lineTotal = 50m },
                    },
                }), [], 100);
            return new("""{"picks":[{"line":1,"sku":null}]}""", [], 10); // the model declines to guess
        };

        var res = await client.PostAsync("/api/ai/documents", Upload());
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Ready", doc.GetProperty("status").GetString());
        var result = doc.GetProperty("result");
        Assert.Equal(supplier, result.GetProperty("supplier").GetProperty("name").GetString());

        var lines = result.GetProperty("lines");
        var first = lines[0];
        Assert.Equal("barcode", first.GetProperty("match").GetString());
        Assert.Equal(product.GetProperty("sku").GetString(), first.GetProperty("sku").GetString());
        Assert.Equal("case", first.GetProperty("unit").GetString()); // the case barcode says it is a case
        Assert.Empty(first.GetProperty("flags").EnumerateArray());

        var second = lines[1];
        var flags = second.GetProperty("flags").EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Contains("no_match", flags);
        Assert.Contains("math", flags); // 1 × 40 ≠ 50
    }

    [Fact]
    public async Task A_reviewed_document_becomes_a_draft_purchase_order_once()
    {
        var (client, llm) = await As("purchasing");
        var product = (await client.GetFromJsonAsync<JsonElement>("/api/products?q=olive oil&pageSize=1")).GetProperty("items")[0];
        var supplier = (await client.GetFromJsonAsync<JsonElement>("/api/suppliers?q=Pantry")).GetProperty("items")[0];
        var chi = (await client.GetFromJsonAsync<JsonElement>("/api/warehouses/directory"))[0].GetProperty("id").GetGuid();
        llm.Reply = (_, messages, schema, _) => messages[0].ImageDataUrl is not null ? new("text", [], 1)
            : schema?["properties"]?["lines"] is not null
                ? new("""{"supplierName":null,"documentNumber":"Q-9","documentDate":null,"currency":null,"subtotal":null,"charges":null,"tax":null,"total":null,"lines":[]}""", [], 1)
                : new("""{"picks":[]}""", [], 1);
        var doc = await (await client.PostAsync("/api/ai/documents", Upload())).Content.ReadFromJsonAsync<JsonElement>();
        var id = doc.GetProperty("id").GetGuid();
        Assert.Contains("supplier_unknown", doc.GetProperty("result").GetProperty("warnings").EnumerateArray().Select(w => w.GetString()));

        var body = new
        {
            supplierId = supplier.GetProperty("id").GetGuid(), warehouseId = chi, note = (string?)null,
            lines = new[] { new { productId = product.GetProperty("id").GetGuid(), quantity = 3m, unit = "case", unitPrice = 60m } },
        };
        var po = await (await client.PostAsJsonAsync($"/api/ai/documents/{id}/purchase-order", body)).Content.ReadFromJsonAsync<JsonElement>();
        var order = await client.GetFromJsonAsync<JsonElement>($"/api/purchase-orders/{po.GetProperty("id")}");
        Assert.Equal("Draft", order.GetProperty("status").GetString());
        Assert.Equal("From supplier document Q-9", order.GetProperty("note").GetString());

        var again = await client.PostAsJsonAsync($"/api/ai/documents/{id}/purchase-order", body);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Unsupported_files_and_scanned_pdfs_are_refused_with_a_reason()
    {
        var (client, _) = await As("purchasing");
        var text = await client.PostAsync("/api/ai/documents", Upload("notes.txt", "text/plain"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, text.StatusCode);

        var scan = await client.PostAsync("/api/ai/documents", Upload("scan.pdf", "application/pdf")); // not a readable PDF
        var doc = await scan.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Failed", doc.GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_chat_tools_run_with_the_askers_permissions()
    {
        var (client, llm) = await As("warehouse"); // Chicago only, no prices
        var round = 0;
        llm.Reply = (_, _, _, tools) =>
        {
            Assert.NotNull(tools);
            return round++ switch
            {
                0 => new(null, [new("1", "receivables", "{}"), new("2", "stock_by_warehouse", """{"query":"greek yogurt"}""")], 50),
                _ => new("Here is what I found.", [], 20),
            };
        };
        var answer = await (await client.PostAsJsonAsync("/api/ai/chat", new { messages = new[] { new { role = "user", content = "who owes us and how much yogurt is there?" } } }))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Here is what I found.", answer.GetProperty("answer").GetString());
        var tables = answer.GetProperty("tables");
        Assert.Equal(["note"], tables[0].GetProperty("columns").EnumerateArray().Select(c => c.GetString())); // receivables: denied
        var warehouses = tables[1].GetProperty("rows").EnumerateArray().Select(r => r[2].GetString()).Distinct().ToList();
        Assert.Equal(["CHI"], warehouses); // stock: only the warehouse this person works in
    }

    [Theory]
    [InlineData("CS", "bottle", "case")]
    [InlineData("ctn", "bottle", "case")]
    [InlineData("carton", "carton", "carton")] // juice is counted in cartons: not a pack here
    [InlineData("EA", "bottle", "bottle")]
    [InlineData("bottles", "bottle", "bottle")]
    [InlineData("pallet", "bottle", null)]
    public void Supplier_units_resolve_to_the_products_units(string printed, string baseUnit, string? expected) =>
        Assert.Equal(expected, Extraction.ResolveUnit(printed, baseUnit, ["case"]));
}
