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

builder.Services.AddHostedService<CoinPriceUpdateService>();

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

// ── Backfill price history in background (non-blocking) ──────────────────────
_ = Task.Run(async () =>
{
    await Task.Delay(5000); // let the app fully start first

    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var gecko = scope.ServiceProvider.GetRequiredService<ICoinGeckoService>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    try
    {
        var hasHistory = await db.CoinPriceHistories.AnyAsync();
        if (hasHistory) return;

        logger.LogInformation("Starting price history backfill...");

        var coins = await db.Coins
            .Where(c => c.IsActive && c.CoinGeckoId != "")
            .ToListAsync();

        foreach (var coin in coins)
        {
            try
            {
                var raw = await gecko.GetMarketChartAsync(coin.CoinGeckoId, 365);
                if (raw == null) continue;

                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                foreach (var point in doc.RootElement.EnumerateArray())
                {
                    var ts = point[0].GetInt64();
                    var price = point[1].GetDecimal();
                    var date = DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime.Date;

                    db.CoinPriceHistories.Add(new MoneroMarketCap.Data.Models.CoinPriceHistory
                    {
                        CoinId = coin.Id,
                        PriceUsd = price,
                        MarketCapUsd = 0,
                        CirculatingSupply = 0,
                        Interval = "1d",
                        RecordedAt = date
                    });
                }

                await db.SaveChangesAsync();
                logger.LogInformation("Backfilled {Name}", coin.Name);
                await Task.Delay(2000);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Backfill failed for {Id}: {Msg}", coin.CoinGeckoId, ex.Message);
            }
        }

        logger.LogInformation("Price history backfill complete.");
    }
    catch (Exception ex)
    {
        var logger2 = app.Services.GetRequiredService<ILogger<Program>>();
        logger2.LogError(ex, "Backfill task failed");
    }
});

// ── Chart endpoint — served from DB ──────────────────────────────────────────
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

    var cutoff = DateTime.UtcNow.Date.AddDays(-365);

    var history = await db.CoinPriceHistories
        .Where(h => h.Coin.CoinGeckoId == coinGeckoId
                 && h.Interval == "1d"
                 && h.RecordedAt >= cutoff)
        .OrderBy(h => h.RecordedAt)
        .Select(h => new double[]
        {
            new DateTimeOffset(h.RecordedAt, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            (double)h.PriceUsd
        })
        .ToListAsync();

    var json = System.Text.Json.JsonSerializer.Serialize(history);
    cache.Set(cacheKey, json, TimeSpan.FromHours(1));
    return Results.Content(json, "application/json");
});

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

app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value;
    if (path != null && path != path.ToLowerInvariant())
    {
        var lower = path.ToLowerInvariant();
        var query = context.Request.QueryString;
        // 308 Permanent Redirect preserves the HTTP method (POST stays POST).
        // 301 downgrades POST to GET, which broke all form submissions.
        context.Response.StatusCode = 308;
        context.Response.Headers.Location = lower + query.ToString();
        return;
    }
    await next();
});

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();