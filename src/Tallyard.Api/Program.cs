using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Tallyard.Api.Common;
using Tallyard.Api.Infrastructure;
using Tallyard.Api.Modules.Audit;
using Tallyard.Api.Modules.Catalog;
using Tallyard.Api.Modules.Identity;
using Tallyard.Api.Modules.Inventory;
using Tallyard.Api.Modules.Partners;
using Tallyard.Api.Modules.Warehouses;
using Tallyard.Api.Seed;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<TenantConnectionInterceptor>();
builder.Services.AddScoped<AuditInterceptor>();
builder.Services.AddScoped<WarehouseAccess>();
builder.Services.AddScoped<DocumentNumbers>();
builder.Services.AddScoped<StockLedger>();
builder.Services.AddScoped<Quantities>();
builder.Services.AddSingleton<IStockNotifier, SignalRStockNotifier>();
builder.Services.AddSignalR();
builder.Services.AddDbContext<AppDbContext>((sp, o) => Db.Configure(o, config.GetConnectionString("App")!)
    .AddInterceptors(sp.GetRequiredService<TenantConnectionInterceptor>(), sp.GetRequiredService<AuditInterceptor>()));

builder.Services.AddHttpContextAccessor();
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddExceptionHandler<ErrorHandler>();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    // Numbers are numbers: no "12.5" strings accepted, so the OpenAPI types say `number`.
    o.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
});
builder.Services.AddOpenApi();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "tallyard";
    o.Cookie.HttpOnly = true;
    // The SPA is served from the same origin, so Strict costs nothing and blocks CSRF.
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromHours(12);
    o.SlidingExpiration = true;
    o.Events = new CookieAuthenticationEvents
    {
        OnValidatePrincipal = SessionValidator.Validate,
        // An API answers 401/403; it doesn't redirect to a login page.
        OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; },
        OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; },
    };
});
builder.Services.AddAuthorization(o =>
{
    o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    foreach (var (perm, roles) in Perm.Roles)
        o.AddPolicy(perm, p => p.RequireRole(roles.Select(r => r.ToString())));
});
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("login", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = config.GetValue("Security:LoginAttemptsPerMinute", 10),
            Window = TimeSpan.FromMinutes(1),
        }));
});

var app = builder.Build();

// `dotnet run -- migrate` / `dotnet run -- seed`: run as the owner role, then exit.
if (args.FirstOrDefault() is "migrate" or "seed")
{
    await using (var owner = Db.Owner(config)) await owner.Database.MigrateAsync();
    if (args[0] == "seed") await DemoSeeder.RunAsync(config);
    return;
}

app.UseExceptionHandler();
// Validation messages (FluentValidation) follow Accept-Language, like the app's own errors.
app.UseRequestLocalization(o => o.SetDefaultCulture("en").AddSupportedUICultures("en", "id").AddSupportedCultures("en"));
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.Use(async (http, next) =>
{
    http.RequestServices.GetRequiredService<CurrentUser>().SetFrom(http.User);
    await next();
});
app.UseAuthorization();
app.UseRateLimiter();

if (app.Environment.IsDevelopment()) app.MapOpenApi().AllowAnonymous();

var api = app.MapGroup("/api");
AuthEndpoints.Map(api);
UserEndpoints.Map(api);
WarehouseEndpoints.Map(api);
CatalogEndpoints.Map(api);
PartnerEndpoints.Map(api);
AuditEndpoints.Map(api);
StockEndpoints.Map(api);
TransferEndpoints.Map(api);
app.MapHub<StockHub>("/hubs/stock");
api.MapGet("/health", () => Results.Ok(new { ok = true })).AllowAnonymous();

// Client-side routes of the Angular app.
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

public partial class Program;
