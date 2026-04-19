using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
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

builder.Services.AddRouting(options =>
{
    options.LowercaseUrls = true;
    options.LowercaseQueryStrings = true;
});

builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<ICoinRepository, CoinRepository>();
builder.Services.AddScoped<IPortfolioRepository, PortfolioRepository>();
builder.Services.AddScoped<IRoleRepository, RoleRepository>();

builder.Services.AddSession();
builder.Services.AddControllersWithViews();

builder.Services.AddMemoryCache();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", p => p.RequireRole(RoleNames.Admin));
    options.AddPolicy("ModeratorOnly", p => p.RequireRole(RoleNames.Moderator, RoleNames.Admin));
    options.AddPolicy("AnyStaff", p => p.RequireRole(RoleNames.Admin, RoleNames.Moderator));
});

builder.Services.AddHttpClient<ICoinGeckoService, CoinGeckoService>();

builder.Services.AddAuthentication("CookieAuth")
    .AddCookie("CookieAuth", options =>
    {
        options.LoginPath = "/Login";
        options.LogoutPath = "/Logout";
    });

var keysPath = builder.Environment.IsDevelopment()
    ? Path.Combine(builder.Environment.ContentRootPath, "dataprotection-keys")
    : "/var/www/moneromarketcap/dataprotection-keys";

Directory.CreateDirectory(keysPath);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
    .SetApplicationName("MoneroMarketCap");

var app = builder.Build();

// ── Migrate-only mode ─────────────────────────────────────────────────────────
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

// ── Chart endpoint — served from DB (worker handles writes) ──────────────────
app.MapGet("/api/coin/{coinGeckoId}/chart", async (
    string coinGeckoId,
    IServiceScopeFactory scopeFactory,
    IMemoryCache cache) =>
{
    var cacheKey = $"chart_{coinGeckoId}";
    if (cache.TryGetValue(cacheKey, out string? cached))
        return Results.Content(cached!, "application/json");

    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    var cutoff = DateTime.UtcNow.Date.AddDays(-364);

    var history = await db.CoinPriceHistories
        .Where(h => h.Coin.CoinGeckoId == coinGeckoId
                 && h.Interval == "1d"
                 && h.RecordedAt >= cutoff)
        .GroupBy(h => h.RecordedAt.Date)
        .Select(g => new
        {
            Date = g.Key,
            Price = g.OrderByDescending(h => h.RecordedAt).First().PriceUsd
        })
        .OrderBy(x => x.Date)
        .ToListAsync();

    var result = history
        .Select(x => new double[]
        {
            new DateTimeOffset(x.Date, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            (double)x.Price
        })
        .ToList();

    var json = System.Text.Json.JsonSerializer.Serialize(result);
    cache.Set(cacheKey, json, TimeSpan.FromHours(1));
    return Results.Content(json, "application/json");
});

// ── Sponsor proxy (/api/sponsors) ────────────────────────────────────────────
var _sponsorCache = string.Empty;
var _sponsorCachedAt = DateTime.MinValue;
var _sponsorCacheTtl = TimeSpan.FromMinutes(
    builder.Configuration.GetValue<int>("Sponsors:CacheTtlMinutes", 5));
var _sponsorLock = new SemaphoreSlim(1, 1);

app.MapGet("/api/sponsors", async (HttpContext ctx, IHttpClientFactory httpFactory, CancellationToken cancel) =>
{
    ctx.Response.Headers["Cache-Control"] = "public, max-age=300";

    if (!string.IsNullOrEmpty(_sponsorCache) && DateTime.UtcNow - _sponsorCachedAt < _sponsorCacheTtl)
    {
        return Results.Content(_sponsorCache, "application/json");
    }

    await _sponsorLock.WaitAsync(cancel);
    try
    {
        if (!string.IsNullOrEmpty(_sponsorCache) && DateTime.UtcNow - _sponsorCachedAt < _sponsorCacheTtl)
        {
            return Results.Content(_sponsorCache, "application/json");
        }

        var url = app.Configuration["Sponsors:SourceUrl"];
        var client = httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        var json = await client.GetStringAsync(url, cancel);
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

// Lowercase URL redirect — GET requests only. POSTs keep their original path
// (Razor Pages routing is case-insensitive, so they match regardless).
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value;
    if (HttpMethods.IsGet(context.Request.Method)
        && path != null
        && path != path.ToLowerInvariant())
    {
        var lower = path.ToLowerInvariant();
        var query = context.Request.QueryString;
        context.Response.StatusCode = 308;
        context.Response.Headers.Location = lower + query.ToString();
        return;
    }
    await next();
});

// Onion-Location header — advertises .onion equivalent to Tor Browser.
var onionHost = builder.Configuration["Tor:OnionHost"];
if (!string.IsNullOrEmpty(onionHost))
{
    app.Use(async (context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            var statusCode = context.Response.StatusCode;
            var contentType = context.Response.ContentType ?? "";
            var path = context.Request.Path.Value ?? "/";

            if (statusCode == 200
                && contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                var onionUrl = $"http://{onionHost}{path}{context.Request.QueryString}";
                context.Response.Headers["Onion-Location"] = onionUrl;
            }
            return Task.CompletedTask;
        });
        await next();
    });
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();