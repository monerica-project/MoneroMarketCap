using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Constants;
using MoneroMarketCap.Data.Repositories;
using MoneroMarketCap.Services.Implementations;
using MoneroMarketCap.Services.Interfaces;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        b => b.MigrationsAssembly("MoneroMarketCap.Data")));

builder.Services.AddRazorPages();

builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<ICoinRepository, CoinRepository>();
builder.Services.AddScoped<IPortfolioRepository, PortfolioRepository>();
builder.Services.AddScoped<IRoleRepository, RoleRepository>();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", p => p.RequireRole(RoleNames.Admin));
    options.AddPolicy("ModeratorOnly", p => p.RequireRole(RoleNames.Moderator, RoleNames.Admin));
    options.AddPolicy("AnyStaff", p => p.RequireRole(RoleNames.Admin, RoleNames.Moderator));
});

builder.Services.AddHttpClient<ICoinGeckoService, CoinGeckoService>();
builder.Services.AddHostedService<CoinPriceUpdateService>();

builder.Services.AddAuthentication("CookieAuth")
    .AddCookie("CookieAuth", options =>
    {
        options.LoginPath = "/Login";
        options.LogoutPath = "/Logout";
    });

var app = builder.Build();

// ── Migrate-only mode: run before any seeding ─────────────────────────────────
if (args.Contains("--migrate-only"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    Console.WriteLine("Migrations complete.");
    return;
}

// ── Seed roles and admin user ─────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    var roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();

    await roles.EnsureRolesSeededAsync();

    var adminUsername = config["Admin:Username"] ?? "admin";
    var adminPassword = config["Admin:Password"] ?? "changeme123";

    if (!await db.Users.AnyAsync(u => u.UserRoles.Any(ur => ur.Role.Name == RoleNames.Admin)))
    {
        var admin = new MoneroMarketCap.Data.Models.AppUser
        {
            Username = adminUsername,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(adminPassword)
        };
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        await roles.AssignRoleAsync(admin.Id, RoleNames.Admin);
    }
}

// ── Sponsor proxy (/api/sponsors) ────────────────────────────────────────────
var _sponsorCache = string.Empty;
var _sponsorCachedAt = DateTime.MinValue;
var _sponsorCacheTtl = TimeSpan.FromMinutes(
    builder.Configuration.GetValue<int>("Sponsors:CacheTtlMinutes", 5));
var _sponsorLock = new SemaphoreSlim(1, 1);

app.MapGet("/api/sponsors", async (IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    if (!string.IsNullOrEmpty(_sponsorCache) && DateTime.UtcNow - _sponsorCachedAt < _sponsorCacheTtl)
        return Results.Content(_sponsorCache, "application/json");

    await _sponsorLock.WaitAsync(ct);
    try
    {
        if (!string.IsNullOrEmpty(_sponsorCache) && DateTime.UtcNow - _sponsorCachedAt < _sponsorCacheTtl)
            return Results.Content(_sponsorCache, "application/json");

        var url = app.Configuration["Sponsors:SourceUrl"];
        var client = httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        var json = await client.GetStringAsync(url, ct);
        _sponsorCache = json;
        _sponsorCachedAt = DateTime.UtcNow;
        return Results.Content(json, "application/json");
    }
    catch
    {
        return Results.Content(string.IsNullOrEmpty(_sponsorCache) ? "[]" : _sponsorCache, "application/json");
    }
    finally { _sponsorLock.Release(); }
});

// ── HTTP pipeline ─────────────────────────────────────────────────────────────
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();