using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace Tallyard.Tests;

[Collection(ApiCollection.Name)]
public class PurchasingTests(ApiFixture api)
{
    record Ids(Guid Supplier, Guid Chi, Guid Product, string Sku);

    async Task<Ids> Setup(HttpClient purchasing)
    {
        var supplier = (await purchasing.GetFromJsonAsync<JsonElement>("/api/suppliers?pageSize=1")).GetProperty("items")[0].GetProperty("id").GetGuid();
        var chi = (await purchasing.GetFromJsonAsync<JsonElement>("/api/warehouses")).EnumerateArray()
            .First(w => w.GetProperty("code").GetString() == "CHI").GetProperty("id").GetGuid();
        var sku = "PO-" + Guid.NewGuid().ToString("N")[..8].ToUpper();
        var res = await purchasing.PostAsJsonAsync("/api/products", new
        {
            sku, name = "Purchasing test " + sku, baseUnit = "pcs", trackBatches = false, taxRate = 10, salePrice = 5, isActive = true,
            units = new[] { new { name = "case", factor = 10 } }, barcodes = Array.Empty<object>(),
        });
        res.EnsureSuccessStatusCode();
        return new Ids(supplier, chi, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid(), sku);
    }

    /// <summary>A submitted order of <paramref name="cases"/> cases at <paramref name="price"/> per case.</summary>
    static async Task<JsonElement> Order(HttpClient c, Ids ids, decimal cases, decimal price, bool submit = true)
    {
        var res = await c.PostAsJsonAsync("/api/purchase-orders", new
        {
            supplierId = ids.Supplier, warehouseId = ids.Chi, expectedOn = (string?)null, note = (string?)null,
            lines = new[] { new { productId = ids.Product, quantity = cases, unit = "case", unitPrice = price, taxRate = (decimal?)null } },
        });
        res.EnsureSuccessStatusCode();
        var po = await res.Content.ReadFromJsonAsync<JsonElement>();
        if (!submit) return po;
        var sub = await c.PostAsync($"/api/purchase-orders/{po.GetProperty("id")}/submit", null);
        sub.EnsureSuccessStatusCode();
        return await sub.Content.ReadFromJsonAsync<JsonElement>();
    }

    static string Status(JsonElement po) => po.GetProperty("status").GetString()!;
    static async Task<string> Code(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

    [Fact]
    public async Task Totals_include_tax_from_the_product_and_small_orders_need_no_approval()
    {
        var buyer = await api.LoginAs("purchasing");
        var ids = await Setup(buyer);
        var po = await Order(buyer, ids, 10, 50); // 500.00 + 10% tax
        Assert.Equal("Approved", Status(po));
        Assert.Equal(500m, po.GetProperty("subtotal").GetDecimal());
        Assert.Equal(550m, po.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task Large_orders_need_finance_then_the_owner_in_that_order_and_never_the_creator()
    {
        var buyer = await api.LoginAs("purchasing");
        var ids = await Setup(buyer);
        var po = await Order(buyer, ids, 300, 50); // 16,500 with tax: levels 1 (finance) and 2 (owner)
        var id = po.GetProperty("id").GetGuid();
        Assert.Equal("PendingApproval", Status(po));
        Assert.Equal(2, po.GetProperty("approvals").GetArrayLength());

        var self = await buyer.PostAsJsonAsync($"/api/purchase-orders/{id}/approve", new { comment = "" });
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        Assert.Equal("po_self_approval", await Code(self));

        var finance = await api.LoginAs("finance");
        (await finance.PostAsJsonAsync($"/api/purchase-orders/{id}/approve", new { comment = "ok" })).EnsureSuccessStatusCode();
        // Finance can't also take the owner's step.
        var again = await finance.PostAsJsonAsync($"/api/purchase-orders/{id}/approve", new { comment = "" });
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);

        var owner = await api.LoginAs("owner");
        var done = await owner.PostAsJsonAsync($"/api/purchase-orders/{id}/approve", new { comment = "" });
        done.EnsureSuccessStatusCode();
        Assert.Equal("Approved", Status(await done.Content.ReadFromJsonAsync<JsonElement>()));
    }

    [Fact]
    public async Task A_rejected_order_goes_back_to_draft_with_the_reason_and_submitted_orders_cannot_be_edited()
    {
        var buyer = await api.LoginAs("purchasing");
        var ids = await Setup(buyer);
        var po = await Order(buyer, ids, 60, 50); // 3,300: finance
        var id = po.GetProperty("id").GetGuid();

        var edit = await buyer.PutAsJsonAsync($"/api/purchase-orders/{id}", new
        {
            supplierId = ids.Supplier, warehouseId = ids.Chi, lines = new[] { new { productId = ids.Product, quantity = 1, unit = "case", unitPrice = 1 } },
        });
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        Assert.Equal("po_not_draft", await Code(edit));

        var finance = await api.LoginAs("finance");
        var noReason = await finance.PostAsJsonAsync($"/api/purchase-orders/{id}/reject", new { comment = " " });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);
        var rejected = await (await finance.PostAsJsonAsync($"/api/purchase-orders/{id}/reject", new { comment = "Price too high" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Draft", Status(rejected));
        Assert.Equal("Price too high", rejected.GetProperty("rejectionReason").GetString());
    }

    [Fact]
    public async Task Partial_receipts_update_the_order_stock_and_average_cost_and_over_receipt_is_refused()
    {
        var buyer = await api.LoginAs("purchasing");
        var ids = await Setup(buyer);
        var po = await Order(buyer, ids, 10, 50); // 10 cases = 100 pcs at 5.00/pcs
        var id = po.GetProperty("id").GetGuid();
        var line = po.GetProperty("lines")[0].GetProperty("id").GetGuid();
        var staff = await api.LoginAs("warehouse");

        (await staff.PostAsJsonAsync($"/api/purchase-orders/{id}/receive", new { lines = new[] { new { lineId = line, quantity = 4 } } })).EnsureSuccessStatusCode();
        var after = await buyer.GetFromJsonAsync<JsonElement>($"/api/purchase-orders/{id}");
        Assert.Equal("PartiallyReceived", Status(after));
        Assert.Equal(40m, after.GetProperty("lines")[0].GetProperty("receivedBase").GetDecimal());

        // 6 cases are left; 5% tolerance allows 6.5, not 7.
        var over = await staff.PostAsJsonAsync($"/api/purchase-orders/{id}/receive", new { lines = new[] { new { lineId = line, quantity = 7 } } });
        Assert.Equal(HttpStatusCode.Conflict, over.StatusCode);
        Assert.Equal("over_receipt", await Code(over));

        (await staff.PostAsJsonAsync($"/api/purchase-orders/{id}/receive", new { lines = new[] { new { lineId = line, quantity = 6 } } })).EnsureSuccessStatusCode();
        Assert.Equal("Received", Status(await buyer.GetFromJsonAsync<JsonElement>($"/api/purchase-orders/{id}")));

        var stock = (await buyer.GetFromJsonAsync<JsonElement>($"/api/stock/{ids.Product}")).GetProperty("warehouses").EnumerateArray()
            .First(w => w.GetProperty("warehouseId").GetGuid() == ids.Chi);
        Assert.Equal(100m, stock.GetProperty("onHand").GetDecimal());
        Assert.Equal(5m, stock.GetProperty("avgCost").GetDecimal()); // cost excludes tax

        // Warehouse staff receive goods but never see prices.
        var seen = await staff.GetFromJsonAsync<JsonElement>($"/api/purchase-orders/{id}");
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("total").ValueKind);
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("lines")[0].GetProperty("unitPrice").ValueKind);
    }

    [Fact]
    public async Task Two_people_receiving_the_rest_at_the_same_time_cannot_both_succeed()
    {
        var buyer = await api.LoginAs("purchasing");
        var ids = await Setup(buyer);
        var po = await Order(buyer, ids, 10, 10);
        var id = po.GetProperty("id").GetGuid();
        var line = po.GetProperty("lines")[0].GetProperty("id").GetGuid();
        var a = await api.LoginAs("warehouse");
        var b = await api.LoginAs("owner");

        var both = await Task.WhenAll(
            a.PostAsJsonAsync($"/api/purchase-orders/{id}/receive", new { lines = new[] { new { lineId = line, quantity = 10 } } }),
            b.PostAsJsonAsync($"/api/purchase-orders/{id}/receive", new { lines = new[] { new { lineId = line, quantity = 10 } } }));
        Assert.Single(both, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(both, r => r.StatusCode == HttpStatusCode.Conflict);
        var stock = (await buyer.GetFromJsonAsync<JsonElement>($"/api/stock/{ids.Product}")).GetProperty("warehouses").EnumerateArray()
            .First(w => w.GetProperty("warehouseId").GetGuid() == ids.Chi);
        Assert.Equal(100m, stock.GetProperty("onHand").GetDecimal());
    }

    [Fact]
    public async Task Reorder_suggestions_count_stock_already_on_order()
    {
        var buyer = await api.LoginAs("purchasing");
        var ids = await Setup(buyer);
        (await buyer.PutAsJsonAsync($"/api/reorder/rules/{ids.Product}", new[]
            { new { warehouseId = ids.Chi, supplierId = ids.Supplier, reorderPoint = 50m, reorderQuantity = 100m } })).EnsureSuccessStatusCode();

        async Task<JsonElement?> Suggestion() =>
            (await buyer.GetFromJsonAsync<JsonElement>("/api/reorder/suggestions")).EnumerateArray()
            .Cast<JsonElement?>().FirstOrDefault(s => s!.Value.GetProperty("sku").GetString() == ids.Sku);

        var s = await Suggestion(); // no stock: suggested
        Assert.NotNull(s);
        Assert.Equal("case", s!.Value.GetProperty("unit").GetString());
        Assert.Equal(15m, s.Value.GetProperty("suggestedQuantity").GetDecimal()); // 50 short + 100, in cases of 10

        var created = await (await buyer.PostAsJsonAsync("/api/reorder/create-orders", new[]
            { new { productId = ids.Product, warehouseId = ids.Chi, quantity = 15m } })).Content.ReadFromJsonAsync<JsonElement>();
        var poId = created[0].GetProperty("id").GetGuid();
        (await buyer.PostAsync($"/api/purchase-orders/{poId}/submit", null)).EnsureSuccessStatusCode(); // small enough to auto-approve

        Assert.Null(await Suggestion()); // 150 incoming covers it
    }

    [Fact]
    public async Task The_purchase_order_pdf_is_a_pdf_and_drafts_have_none()
    {
        var buyer = await api.LoginAs("purchasing");
        var ids = await Setup(buyer);
        var draft = await Order(buyer, ids, 1, 10, submit: false);
        Assert.Equal(HttpStatusCode.Conflict, (await buyer.GetAsync($"/api/purchase-orders/{draft.GetProperty("id")}/pdf")).StatusCode);

        var po = await Order(buyer, ids, 1, 10);
        var res = await buyer.GetAsync($"/api/purchase-orders/{po.GetProperty("id")}/pdf");
        res.EnsureSuccessStatusCode();
        Assert.Equal("application/pdf", res.Content.Headers.ContentType!.MediaType);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
    }

    [Fact]
    public async Task No_row_refers_to_another_tenants_product_or_warehouse()
    {
        await using var conn = new NpgsqlConnection(api.OwnerConnection);
        await conn.OpenAsync();
        foreach (var (table, column, target) in new[]
        {
            ("stock_balances", "product_id", "products"), ("stock_balances", "warehouse_id", "warehouses"),
            ("stock_movements", "product_id", "products"), ("purchase_order_lines", "product_id", "products"),
            ("purchase_orders", "supplier_id", "suppliers"), ("purchase_orders", "warehouse_id", "warehouses"),
            ("reorder_rules", "product_id", "products"), ("batches", "product_id", "products"),
        })
        {
            await using var cmd = new NpgsqlCommand(
                $"SELECT count(*) FROM {table} x JOIN {target} t ON t.id = x.{column} WHERE t.tenant_id <> x.tenant_id", conn);
            Assert.True(0L == (long)(await cmd.ExecuteScalarAsync())!, $"{table}.{column} crosses tenants");
        }
    }
}
