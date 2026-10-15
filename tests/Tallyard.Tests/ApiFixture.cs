using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Tallyard.Api.Infrastructure;
using Tallyard.Api.Seed;
using Testcontainers.PostgreSql;

namespace Tallyard.Tests;

/// <summary>
/// A real Postgres in Docker, set up exactly like production (same init script, so the app
/// connects as the non-owner role that RLS applies to), migrated and seeded with the demo data.
/// </summary>
public sealed class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    readonly PostgreSqlContainer pg = new PostgreSqlBuilder()
        .WithImage("postgres:16")
        .WithDatabase("tallyard").WithUsername("tallyard").WithPassword("tallyard")
        .WithResourceMapping(new FileInfo(Path.Combine(RepoRoot(), "docker", "init.sql")), "/docker-entrypoint-initdb.d/")
        .Build();

    public string OwnerConnection => pg.GetConnectionString();
    public string AppConnection => new NpgsqlConnectionStringBuilder(OwnerConnection) { Username = "tallyard_app", Password = "tallyard_app" }.ToString();

    public IConfiguration Config => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:Owner"] = OwnerConnection,
        ["ConnectionStrings:App"] = AppConnection,
    }).Build();

    public async Task InitializeAsync()
    {
        await pg.StartAsync();
        await using (var owner = Db.Owner(Config)) await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.MigrateAsync(owner.Database);
        await DemoSeeder.RunAsync(Config);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await pg.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:App", AppConnection);
        builder.UseSetting("ConnectionStrings:Owner", OwnerConnection);
        // Every test signs in; the per-IP login limit is covered by its own test.
        builder.UseSetting("Security:LoginAttemptsPerMinute", "1000");
    }

    /// <summary>A client signed in as one of the demo users (cookies are kept per client).</summary>
    public async Task<HttpClient> LoginAs(string local, string workspace = "harborpine")
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var domain = workspace + ".test";
        var res = await client.PostAsJsonAsync("/api/auth/login",
            new { workspace, email = $"{local}@{domain}", password = DemoSeeder.Password });
        res.EnsureSuccessStatusCode();
        return client;
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docker-compose.yml"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}
