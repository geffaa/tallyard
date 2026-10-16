using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace Tallyard.Tests;

[Collection(ApiCollection.Name)]
public class SalesTests(ApiFixture api)
{
    record World(Guid Chi, Guid Product, Guid Customer);

    /// <summary>A fresh product with <paramref name="stock"/> pcs in Chicago, and a customer with the given limit.</summary>
    async Task<World> Setup(decimal stock, decimal creditLimit = 1_000_000, int terms = 30)
    {
        var owner = await api.LoginAs("owner");
        var chi = (await owner.GetFromJsonAsync<JsonElement>("/api/warehouses")).EnumerateArray()
            .First(w => w.GetProperty("code").GetString() == "CHI").GetProperty("id").GetGuid();
        var sku = "S-" + Guid.NewGuid().ToString("N")[..8].ToUpper();
        var p = await owner.PostAsJsonAsync("/api/products", new
        {
            sku, name = "Sales test " + sku, baseUnit = "pcs", trackBatches = false, taxRate = 10, salePrice = 2, isActive = true,
            units = new[] { new { name = "case", factor = 12 } }, barcodes = Array.Empty<object>(),
        });
        p.EnsureSuccessStatusCode();
        var product = (await p.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        if (stock > 0)
            (await owner.PostAsJsonAsync("/api/stock/adjustments", new
            {
                warehouseId = chi, reason = "found",
                lines = new[] { new { productId = product, quantity = stock, unitCost = 1m } },
            })).EnsureSuccessStatusCode();
        var c = await owner.PostAsJsonAsync("/api/customers", new
        {
            code = "C" + Guid.NewGuid().ToString("N")[..8].ToUpper(), name = "Customer " + sku, paymentTermsDays = terms,
            isActive = true, creditLimit,
        });
        c.EnsureSuccessStatusCode();
        return new World(chi, product, (await c.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
    }

    static async Task<JsonElement> Order(HttpClient c, World w, decimal pcs, decimal? price = null)
    {
        var res = await c.PostAsJsonAsync("/api/sales-orders", new
        {
            customerId = w.Customer, warehouseId = w.Chi,
            lines = new[] { new { productId = w.Product, quantity = pcs, unit = "pcs", unitPrice = price } },
        });
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    static Task<HttpResponseMessage> Confirm(HttpClient c, JsonElement so, bool backorder = false) =>
        c.PostAsJsonAsync($"/api/sales-orders/{so.GetProperty("id")}/confirm", new { allowBackorder = backorder });

    static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    static async Task<decimal> Available(HttpClient c, World w) =>
        (await c.GetFromJsonAsync<JsonElement>($"/api/stock/{w.Product}")).GetProperty("warehouses").EnumerateArray()
        .First(x => x.GetProperty("warehouseId").GetGuid() == w.Chi).GetProperty("available").GetDecimal();

    [Fact]
    public async Task Confirming_reserves_stock_and_two_orders_cannot_both_take_the_last_units()
    {
        var w = await Setup(10);
        var sales = await api.LoginAs("sales");
        var a = await Order(sales, w, 8);
        var b = await Order(sales, w, 8);

        var both = await Task.WhenAll(Confirm(sales, a), Confirm(sales, b));
        Assert.Single(both, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(both, r => r.StatusCode == HttpStatusCode.Conflict); // insufficient_stock
        Assert.Equal(2m, await Available(sales, w));
    }

    [Fact]
    public async Task Backorders_reserve_what_is_there_and_cancelling_gives_it_back()
    {
        var w = await Setup(5);
        var sales = await api.LoginAs("sales");
        var so = await Json(await Confirm(sales, await Order(sales, w, 8), backorder: true));
        var line = so.GetProperty("lines")[0];
        Assert.Equal(5m, line.GetProperty("reserved").GetDecimal());
        Assert.Equal(3m, line.GetProperty("backordered").GetDecimal());
        Assert.Equal(0m, await Available(sales, w));

        await Json(await sales.PostAsync($"/api/sales-orders/{so.GetProperty("id")}/cancel", null));
        Assert.Equal(5m, await Available(sales, w));
    }

    [Fact]
    public async Task Only_reserved_stock_ships_and_each_shipment_is_invoiced_once()
    {
        var w = await Setup(24);
        var sales = await api.LoginAs("sales");
        var so = await Json(await Confirm(sales, await Order(sales, w, 20, price: 3)));
        var id = so.GetProperty("id").GetGuid();
        var lineId = so.GetProperty("lines")[0].GetProperty("id").GetGuid();
        var owner = await api.LoginAs("owner");

        var tooMuch = await owner.PostAsJsonAsync($"/api/sales-orders/{id}/ship", new { lines = new[] { new { lineId, quantity = 21 } } });
        Assert.Equal(HttpStatusCode.Conflict, tooMuch.StatusCode);

        var shipped = await Json(await owner.PostAsJsonAsync($"/api/sales-orders/{id}/ship", new { lines = new[] { new { lineId, quantity = 12 } } }));
        var after = await owner.GetFromJsonAsync<JsonElement>($"/api/sales-orders/{id}");
        Assert.Equal("PartiallyShipped", after.GetProperty("status").GetString());
        Assert.Equal(8m, after.GetProperty("lines")[0].GetProperty("reserved").GetDecimal());

        var finance = await api.LoginAs("finance");
        var shipmentId = shipped.GetProperty("id").GetGuid();
        var twice = await Task.WhenAll(
            finance.PostAsJsonAsync($"/api/shipments/{shipmentId}/invoice", new { issueDate = (string?)null }),
            finance.PostAsJsonAsync($"/api/shipments/{shipmentId}/invoice", new { issueDate = (string?)null }));
        Assert.Single(twice, r => r.StatusCode == HttpStatusCode.OK);
        var invoiceId = (await twice.First(r => r.IsSuccessStatusCode).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var invoice = await finance.GetFromJsonAsync<JsonElement>($"/api/invoices/{invoiceId}");
        Assert.Equal(39.60m, invoice.GetProperty("total").GetDecimal()); // 12 × 3.00 + 10% tax: only what shipped is billed
    }

    [Fact]
    public async Task Over_the_credit_limit_the_order_waits_for_finance()
    {
        var w = await Setup(100, creditLimit: 50);
        var sales = await api.LoginAs("sales");
        var so = await Json(await Confirm(sales, await Order(sales, w, 30, price: 2))); // 66 with tax > 50
        var id = so.GetProperty("id").GetGuid();
        Assert.Equal("OnHold", so.GetProperty("status").GetString());
        Assert.Equal("credit_limit", so.GetProperty("holdReason").GetString());
        Assert.Equal(100m, await Available(sales, w)); // nothing reserved while on hold

        Assert.Equal(HttpStatusCode.Forbidden, (await sales.PostAsJsonAsync($"/api/sales-orders/{id}/release", new { note = "please" })).StatusCode);
        var finance = await api.LoginAs("finance");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await finance.PostAsJsonAsync($"/api/sales-orders/{id}/release", new { note = "" })).StatusCode);
        var released = await Json(await finance.PostAsJsonAsync($"/api/sales-orders/{id}/release", new { note = "Paid upfront" }));
        Assert.Equal("Confirmed", released.GetProperty("status").GetString());
        Assert.Equal(70m, await Available(sales, w));
    }

    [Fact]
    public async Task Payments_settle_the_oldest_invoices_first_and_cannot_exceed_what_is_owed()
    {
        var w = await Setup(100);
        var sales = await api.LoginAs("sales");
        var owner = await api.LoginAs("owner");
        var finance = await api.LoginAs("finance");
        // Ship both before billing: once the first (back-dated) invoice is overdue, new orders are held.
        var shipments = new List<Guid>();
        for (var i = 0; i < 2; i++)
        {
            var so = await Json(await Confirm(sales, await Order(sales, w, 10, price: 10))); // 110.00 each
            var line = so.GetProperty("lines")[0].GetProperty("id").GetGuid();
            var ship = await Json(await owner.PostAsJsonAsync($"/api/sales-orders/{so.GetProperty("id")}/ship", new { lines = new[] { new { lineId = line, quantity = 10 } } }));
            shipments.Add(ship.GetProperty("id").GetGuid());
        }
        var invoices = new List<Guid>();
        foreach (var (shipment, issued) in shipments.Zip(["2026-07-01", "2026-08-01"]))
            invoices.Add((await Json(await finance.PostAsJsonAsync($"/api/shipments/{shipment}/invoice", new { issueDate = issued }))).GetProperty("id").GetGuid());

        var over = await finance.PostAsJsonAsync("/api/payments", new { customerId = w.Customer, amount = 300m, method = "BankTransfer" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, over.StatusCode);

        await Json(await finance.PostAsJsonAsync("/api/payments", new { customerId = w.Customer, amount = 150m, method = "BankTransfer", reference = "T-1" }));
        var first = await finance.GetFromJsonAsync<JsonElement>($"/api/invoices/{invoices[0]}");
        var second = await finance.GetFromJsonAsync<JsonElement>($"/api/invoices/{invoices[1]}");
        Assert.Equal("Paid", first.GetProperty("status").GetString());
        Assert.Equal("PartiallyPaid", second.GetProperty("status").GetString());
        Assert.Equal(70m, second.GetProperty("balance").GetDecimal());

        // Both invoices are long overdue: the aging report puts them in the right buckets, and
        // a new order for this customer is held regardless of the limit.
        var aging = (await finance.GetFromJsonAsync<JsonElement>("/api/receivables/aging")).EnumerateArray()
            .First(r => r.GetProperty("customerId").GetGuid() == w.Customer);
        Assert.Equal(70m, aging.GetProperty("total").GetDecimal());
        var held = await Json(await Confirm(sales, await Order(sales, w, 1)));
        Assert.Equal("overdue", held.GetProperty("holdReason").GetString());
    }

    [Fact]
    public async Task Warehouse_staff_ship_without_seeing_prices_and_cannot_see_invoices()
    {
        var w = await Setup(12);
        var sales = await api.LoginAs("sales");
        var so = await Json(await Confirm(sales, await Order(sales, w, 12, price: 5)));
        var staff = await api.LoginAs("warehouse");
        var seen = await staff.GetFromJsonAsync<JsonElement>($"/api/sales-orders/{so.GetProperty("id")}");
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("total").ValueKind);
        Assert.Contains("ship", seen.GetProperty("actions").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync("/api/invoices")).StatusCode);
    }

    [Fact]
    public async Task Reservations_on_balances_always_match_the_open_order_lines()
    {
        await using var conn = new NpgsqlConnection(api.OwnerConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT count(*) FROM stock_balances b
            WHERE b.reserved <> (SELECT coalesce(sum(l.reserved_base), 0) FROM sales_order_lines l
                                 JOIN sales_orders o ON o.id = l.sales_order_id
                                 WHERE l.product_id = b.product_id AND o.warehouse_id = b.warehouse_id)
            """, conn);
        Assert.Equal(0L, await cmd.ExecuteScalarAsync());
    }
}
