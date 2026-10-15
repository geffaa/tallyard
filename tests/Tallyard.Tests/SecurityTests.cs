using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace Tallyard.Tests;

[Collection(ApiCollection.Name)]
public class SecurityTests(ApiFixture api)
{
    record PageOf<T>(List<T> Items, int Total);
    record Item(Guid Id, string Sku, string Name);
    record Wh(Guid Id, string Code);

    [Fact]
    public async Task Tenants_never_see_each_others_rows_through_the_api()
    {
        var harbor = await api.LoginAs("owner");
        var north = await api.LoginAs("owner", "northpeak");

        var hp = await harbor.GetFromJsonAsync<PageOf<Item>>("/api/products?pageSize=200");
        var np = await north.GetFromJsonAsync<PageOf<Item>>("/api/products?pageSize=200");
        Assert.Equal(400, hp!.Total);
        Assert.Equal(30, np!.Total);

        // A direct request for another tenant's record is a 404, not a 403: its existence isn't revealed.
        var res = await north.GetAsync($"/api/products/{hp.Items[0].Id}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Row_level_security_holds_even_for_sql_without_a_tenant_filter()
    {
        // As the app role, a query with no WHERE clause at all.
        await using var conn = new NpgsqlConnection(api.AppConnection);
        await conn.OpenAsync();
        await using var count = new NpgsqlCommand("SELECT count(*) FROM products", conn);
        Assert.Equal(0L, await count.ExecuteScalarAsync()); // no tenant in scope: nothing

        await using var tenant = new NpgsqlCommand("SELECT id FROM tenants WHERE slug = 'northpeak'", conn);
        var northId = (Guid)(await tenant.ExecuteScalarAsync())!;
        await using var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{northId}', false)", conn);
        await set.ExecuteNonQueryAsync();
        Assert.Equal(30L, await count.ExecuteScalarAsync());

        // Writing a row for another tenant is rejected by the policy's WITH CHECK.
        await using var insert = new NpgsqlCommand(
            "INSERT INTO categories (id, tenant_id, name, created_at, updated_at) " +
            "SELECT gen_random_uuid(), id, 'x', now(), now() FROM tenants WHERE slug = 'harborpine'", conn);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState); // insufficient_privilege (row violates RLS policy)
    }

    [Fact]
    public async Task Audit_log_is_append_only_even_for_the_table_owner()
    {
        var owner = await api.LoginAs("owner");
        var wh = (await owner.GetFromJsonAsync<List<Wh>>("/api/warehouses"))![0];
        var res = await owner.PutAsJsonAsync($"/api/warehouses/{wh.Id}",
            new { code = wh.Code, name = "Chicago Distribution Center", city = "Chicago", isActive = true });
        res.EnsureSuccessStatusCode();

        var audit = await owner.GetFromJsonAsync<JsonElement>($"/api/audit?entityId={wh.Id}");
        Assert.Contains(audit.GetProperty("items").EnumerateArray(), e => e.GetProperty("action").GetString() == "updated");

        await using var conn = new NpgsqlConnection(api.OwnerConnection);
        await conn.OpenAsync();
        foreach (var sql in new[] { "UPDATE audit_logs SET action = 'x'", "DELETE FROM audit_logs", "TRUNCATE audit_logs" })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Contains("append-only", ex.MessageText);
        }

        // The app role has no UPDATE privilege in the first place.
        await using var app = new NpgsqlConnection(api.AppConnection);
        await app.OpenAsync();
        await using var upd = new NpgsqlCommand("UPDATE audit_logs SET action = 'x'", app);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => upd.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    public async Task Warehouse_staff_only_see_their_warehouse_and_cannot_edit_master_data()
    {
        var staff = await api.LoginAs("warehouse");
        var whs = await staff.GetFromJsonAsync<List<Wh>>("/api/warehouses");
        Assert.Equal(["CHI"], whs!.Select(w => w.Code));

        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PostAsJsonAsync("/api/categories", new { name = "New" })).StatusCode);
    }

    [Fact]
    public async Task Deactivating_a_user_ends_their_session_immediately()
    {
        var owner = await api.LoginAs("owner");
        var finance = await api.LoginAs("finance");
        Assert.Equal(HttpStatusCode.OK, (await finance.GetAsync("/api/auth/me")).StatusCode);

        var users = await owner.GetFromJsonAsync<JsonElement>("/api/users?q=finance");
        var u = users.GetProperty("items")[0];
        var body = new
        {
            name = u.GetProperty("name").GetString(), email = u.GetProperty("email").GetString(),
            role = "Finance", isActive = false, allWarehouses = true, warehouseIds = Array.Empty<Guid>(),
            version = u.GetProperty("version").GetUInt32(),
        };
        (await owner.PutAsJsonAsync($"/api/users/{u.GetProperty("id").GetGuid()}", body)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Unauthorized, (await finance.GetAsync("/api/auth/me")).StatusCode);

        (await owner.PutAsJsonAsync($"/api/users/{u.GetProperty("id").GetGuid()}",
            body with { isActive = true, version = (await owner.GetFromJsonAsync<JsonElement>("/api/users?q=finance"))
                .GetProperty("items")[0].GetProperty("version").GetUInt32() })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Wrong_password_and_unknown_workspace_look_the_same()
    {
        var client = api.CreateClient();
        var a = await client.PostAsJsonAsync("/api/auth/login", new { workspace = "harborpine", email = "owner@harborpine.test", password = "nope" });
        var b = await client.PostAsJsonAsync("/api/auth/login", new { workspace = "nosuch", email = "owner@harborpine.test", password = "nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, a.StatusCode);
        Assert.Equal(await a.Content.ReadAsStringAsync(), await b.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/products")).StatusCode);
    }

    [Fact]
    public async Task The_last_owner_cannot_be_demoted()
    {
        var owner = await api.LoginAs("owner");
        var u = (await owner.GetFromJsonAsync<JsonElement>("/api/users?q=owner@")).GetProperty("items")[0];
        var res = await owner.PutAsJsonAsync($"/api/users/{u.GetProperty("id").GetGuid()}", new
        {
            name = u.GetProperty("name").GetString(), email = u.GetProperty("email").GetString(),
            role = "Sales", isActive = true, allWarehouses = true, warehouseIds = Array.Empty<Guid>(),
            version = u.GetProperty("version").GetUInt32(),
        });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("last_owner", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }
}

public class LoginRateLimitTests
{
    [Fact]
    public async Task Repeated_login_attempts_from_one_address_are_throttled()
    {
        // No database needed: the limiter runs before the endpoint.
        await using var app = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("Security:LoginAttemptsPerMinute", "3"));
        var client = app.CreateClient();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
            codes.Add((await client.PostAsJsonAsync("/api/auth/login", new { workspace = "", email = "", password = "" })).StatusCode);
        Assert.Equal([HttpStatusCode.UnprocessableEntity, HttpStatusCode.UnprocessableEntity, HttpStatusCode.UnprocessableEntity, HttpStatusCode.TooManyRequests], codes);
    }
}
