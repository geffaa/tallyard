using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Tallyard.Api.Infrastructure;

public static class Db
{
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder o, string connectionString) =>
        o.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();

    /// <summary>A context on the owner role, which bypasses RLS: for migrations and seeding only.</summary>
    public static AppDbContext Owner(IConfiguration config, CurrentUser? user = null)
    {
        var o = new DbContextOptionsBuilder<AppDbContext>();
        Configure(o, config.GetConnectionString("Owner")!);
        return new AppDbContext(o.Options, user ?? new CurrentUser());
    }
}

public static class DbApp
{
    /// <summary>A context as the application role, scoped to the tenant in <paramref name="user"/> by
    /// RLS, with the same interceptors as a request. For background work and seeding.</summary>
    public static AppDbContext Create(IConfiguration config, CurrentUser user)
    {
        var o = new DbContextOptionsBuilder<AppDbContext>();
        Db.Configure(o, config.GetConnectionString("App")!)
            .AddInterceptors(new TenantConnectionInterceptor(user), new AuditInterceptor(user));
        return new AppDbContext(o.Options, user);
    }
}

public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) => Db.Owner(new ConfigurationBuilder()
        .AddJsonFile("appsettings.json").AddJsonFile("appsettings.Development.json", optional: true).Build());
}
