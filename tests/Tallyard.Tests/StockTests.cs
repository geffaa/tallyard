using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace Tallyard.Tests;

[Collection(ApiCollection.Name)]
public class StockTests(ApiFixture api)
{
    static async Task<Dictionary<string, Guid>> Warehouses(HttpClient c) =>
        (await c.GetFromJsonAsync<JsonElement>("/api/warehouses")).EnumerateArray()
        .ToDictionary(w => w.GetProperty("code").GetString()!, w => w.GetProperty("id").GetGuid());

    /// <summary>A new product (no history), so each test starts from known stock.</summary>
    static async Task<Guid> NewProduct(HttpClient c, bool batches = false, string unit = "pcs")
    {
        var sku = "T-" + Guid.NewGuid().ToString("N")[..10].ToUpper();
        var res = await c.PostAsJsonAsync("/api/products", new
        {
            sku, name = "Test " + sku, baseUnit = unit, trackBatches = batches, taxRate = 0, salePrice = 10, isActive = true,
            units = new[] { new { name = "case", factor = 12 } }, barcodes = Array.Empty<object>(),
        });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    static Task<HttpResponseMessage> Adjust(HttpClient c, Guid warehouse, Guid product, decimal qty,
        string reason = "found", decimal? cost = null, string? batch = null, string? expires = null, string? unit = null, string? key = null) =>
        c.PostAsJsonAsync("/api/stock/adjustments", new
        {
            warehouseId = warehouse, reason, note = (string?)null, idempotencyKey = key,
            lines = new[] { new { productId = product, quantity = qty, unit, batchNumber = batch, expiresOn = expires, unitCost = cost } },
        });

    static async Task<JsonElement> StockIn(HttpClient c, Guid product, Guid warehouse) =>
        (await c.GetFromJsonAsync<JsonElement>($"/api/stock/{product}")).GetProperty("warehouses").EnumerateArray()
        .First(w => w.GetProperty("warehouseId").GetGuid() == warehouse);

    [Fact]
    public async Task Fifty_parallel_issues_of_ten_units_succeed_exactly_ten_times()
    {
        var owner = await api.LoginAs("owner");
        var chi = (await Warehouses(owner))["CHI"];
        var product = await NewProduct(owner);
        (await Adjust(owner, chi, product, 10, cost: 2)).EnsureSuccessStatusCode();

        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Adjust(owner, chi, product, -1, "damaged")));

        Assert.Equal(10, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(40, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        var codes = await Task.WhenAll(results.Where(r => !r.IsSuccessStatusCode)
            .Select(async r => (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()));
        Assert.All(codes, c => Assert.Equal("insufficient_stock", c));
        Assert.Equal(0m, (await StockIn(owner, product, chi)).GetProperty("onHand").GetDecimal());
    }

    [Fact]
    public async Task Balances_always_equal_the_sum_of_their_movements()
    {
        var owner = await api.LoginAs("owner");
        var w = await Warehouses(owner);
        var product = await NewProduct(owner, batches: true);
        (await Adjust(owner, w["CHI"], product, 30, cost: 1, batch: "A1", expires: "2027-01-31")).EnsureSuccessStatusCode();
        (await Adjust(owner, w["CHI"], product, 20, cost: 1, batch: "B2", expires: "2026-12-31")).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync("/api/transfers", new
        {
            fromWarehouseId = w["CHI"], toWarehouseId = w["DAL"], note = "t",
            lines = new[] { new { productId = product, quantity = 25m, unit = (string?)null, batchNumber = (string?)null } },
        })).EnsureSuccessStatusCode();
        (await Adjust(owner, w["CHI"], product, -3, "damaged")).EnsureSuccessStatusCode();

        // Checked across the whole database (all tenants), as the owner role.
        await using var conn = new NpgsqlConnection(api.OwnerConnection);
        await conn.OpenAsync();
        foreach (var sql in new[]
        {
            """
            SELECT count(*) FROM stock_balances b
            WHERE b.on_hand <> (SELECT coalesce(sum(quantity), 0) FROM stock_movements m
                                WHERE m.product_id = b.product_id AND m.warehouse_id = b.warehouse_id)
            """,
            """
            SELECT count(*) FROM batch_balances bb
            WHERE bb.on_hand <> (SELECT coalesce(sum(quantity), 0) FROM stock_movements m
                                 WHERE m.batch_id = bb.batch_id AND m.warehouse_id = bb.warehouse_id)
            """,
            // The stock card reads by date: each running balance follows from the one before it.
            """
            SELECT count(*) FROM (
                SELECT balance_after, quantity,
                       lag(balance_after) OVER (PARTITION BY product_id, warehouse_id ORDER BY created_at, id) AS prev
                FROM stock_movements) m
            WHERE prev IS NOT NULL AND balance_after <> prev + quantity
            """,
            """
            SELECT count(*) FROM stock_balances b JOIN products p ON p.id = b.product_id
            WHERE p.track_batches AND b.on_hand <> (SELECT coalesce(sum(on_hand), 0) FROM batch_balances bb
                                                    WHERE bb.product_id = b.product_id AND bb.warehouse_id = b.warehouse_id)
            """,
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            Assert.Equal(0L, await cmd.ExecuteScalarAsync());
        }
    }

    [Fact]
    public async Task Issues_take_the_batch_that_expires_first()
    {
        var owner = await api.LoginAs("owner");
        var chi = (await Warehouses(owner))["CHI"];
        var product = await NewProduct(owner, batches: true);
        (await Adjust(owner, chi, product, 10, cost: 1, batch: "LATE", expires: "2027-06-30")).EnsureSuccessStatusCode();
        (await Adjust(owner, chi, product, 10, cost: 1, batch: "EARLY", expires: "2026-11-30")).EnsureSuccessStatusCode();

        (await Adjust(owner, chi, product, -14, "damaged")).EnsureSuccessStatusCode();

        var batches = (await owner.GetFromJsonAsync<JsonElement>($"/api/stock/{product}")).GetProperty("batches")
            .EnumerateArray().ToDictionary(b => b.GetProperty("number").GetString()!, b => b.GetProperty("onHand").GetDecimal());
        Assert.False(batches.ContainsKey("EARLY")); // used up first
        Assert.Equal(6m, batches["LATE"]);
    }

    [Fact]
    public async Task Receiving_a_batch_tracked_product_requires_a_batch()
    {
        var owner = await api.LoginAs("owner");
        var chi = (await Warehouses(owner))["CHI"];
        var product = await NewProduct(owner, batches: true);
        var res = await Adjust(owner, chi, product, 5, cost: 1);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Equal("batch_required", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Moving_average_cost_and_pack_units()
    {
        var owner = await api.LoginAs("owner");
        var chi = (await Warehouses(owner))["CHI"];
        var product = await NewProduct(owner);
        (await Adjust(owner, chi, product, 10, cost: 2)).EnsureSuccessStatusCode();           // 10 @ 2.00
        (await Adjust(owner, chi, product, 1, cost: 36, unit: "case")).EnsureSuccessStatusCode(); // 12 @ 3.00 per pcs

        var s = await StockIn(owner, product, chi);
        Assert.Equal(22m, s.GetProperty("onHand").GetDecimal());
        Assert.Equal(Math.Round((10 * 2m + 12 * 3m) / 22, 4), s.GetProperty("avgCost").GetDecimal());

        // Warehouse staff see quantities but not costs.
        var staff = await api.LoginAs("warehouse");
        Assert.Equal(JsonValueKind.Null, (await StockIn(staff, product, chi)).GetProperty("avgCost").ValueKind);
    }

    [Fact]
    public async Task A_retried_request_with_the_same_key_posts_once()
    {
        var owner = await api.LoginAs("owner");
        var chi = (await Warehouses(owner))["CHI"];
        var product = await NewProduct(owner);
        var key = Guid.NewGuid().ToString();
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Adjust(owner, chi, product, 7, cost: 1, key: key)));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var numbers = await Task.WhenAll(results.Select(async r => (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("number").GetString()));
        Assert.Single(numbers.Distinct());
        Assert.Equal(7m, (await StockIn(owner, product, chi)).GetProperty("onHand").GetDecimal());
    }

    [Fact]
    public async Task Transferred_stock_is_in_neither_warehouse_until_received_and_keeps_its_cost()
    {
        var owner = await api.LoginAs("owner");
        var w = await Warehouses(owner);
        var product = await NewProduct(owner);
        (await Adjust(owner, w["CHI"], product, 24, cost: 5)).EnsureSuccessStatusCode();

        var res = await owner.PostAsJsonAsync("/api/transfers", new
        {
            fromWarehouseId = w["CHI"], toWarehouseId = w["DAL"], note = (string?)null,
            lines = new[] { new { productId = product, quantity = 1m, unit = "case", batchNumber = (string?)null } },
        });
        res.EnsureSuccessStatusCode();
        var transfer = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InTransit", transfer.GetProperty("status").GetString());
        Assert.Equal(12m, (await StockIn(owner, product, w["CHI"])).GetProperty("onHand").GetDecimal());
        Assert.Equal(0m, (await StockIn(owner, product, w["DAL"])).GetProperty("onHand").GetDecimal());

        var id = transfer.GetProperty("id").GetGuid();
        var both = await Task.WhenAll(owner.PostAsync($"/api/transfers/{id}/receive", null), owner.PostAsync($"/api/transfers/{id}/receive", null));
        Assert.Single(both, r => r.StatusCode == HttpStatusCode.OK);   // received once, even if clicked twice
        Assert.Single(both, r => r.StatusCode == HttpStatusCode.Conflict);

        var dal = await StockIn(owner, product, w["DAL"]);
        Assert.Equal(12m, dal.GetProperty("onHand").GetDecimal());
        Assert.Equal(5m, dal.GetProperty("avgCost").GetDecimal());
    }

    [Fact]
    public async Task Warehouse_staff_cannot_move_stock_in_a_warehouse_they_cannot_see()
    {
        var owner = await api.LoginAs("owner");
        var w = await Warehouses(owner);
        var product = await NewProduct(owner);
        var staff = await api.LoginAs("warehouse"); // Chicago only
        Assert.Equal(HttpStatusCode.Forbidden, (await Adjust(staff, w["DAL"], product, 5)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Adjust(staff, w["CHI"], product, 5)).StatusCode);

        var sales = await api.LoginAs("sales");
        Assert.Equal(HttpStatusCode.Forbidden, (await Adjust(sales, w["CHI"], product, 5)).StatusCode);
    }

    [Fact]
    public async Task Movements_cannot_be_changed_and_balances_cannot_go_negative_even_in_sql()
    {
        await using var conn = new NpgsqlConnection(api.OwnerConnection);
        await conn.OpenAsync();
        await using var upd = new NpgsqlCommand("UPDATE stock_movements SET quantity = quantity + 1", conn);
        Assert.Contains("append-only", (await Assert.ThrowsAsync<PostgresException>(() => upd.ExecuteNonQueryAsync())).MessageText);
        await using var neg = new NpgsqlCommand("UPDATE stock_balances SET on_hand = -1 WHERE ctid = (SELECT ctid FROM stock_balances LIMIT 1)", conn);
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => neg.ExecuteNonQueryAsync())).SqlState); // check_violation
    }

    [Fact]
    public async Task Every_table_with_a_tenant_column_has_row_level_security()
    {
        await using var conn = new NpgsqlConnection(api.OwnerConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT string_agg(c.relname, ', ') FROM pg_class c
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = 'tenant_id'
            JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = 'public'
            WHERE c.relkind IN ('r', 'p') AND NOT c.relrowsecurity
              AND NOT c.relispartition
            """, conn);
        Assert.Equal(DBNull.Value, await cmd.ExecuteScalarAsync());
    }
}
