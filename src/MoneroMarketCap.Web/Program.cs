using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Constants;
using MoneroMarketCap.Data.Repositories;
using MoneroMarketCap.Services.Implementations;
using MoneroMarketCap.Services.Interfaces;
using MoneroMarketCap.Services.Models;

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

// FX rates: the Web app only READS rates from the DB. The actual upstream refresh
// runs in the Worker process. We still register the typed HttpClient here because
// the service signature requires it (read paths never use it).
builder.Services.AddHttpClient<IFiatRateService, FiatRateService>();

// Historical FX: same deal. The Worker fetches from frankfurter.app and writes
// to FiatRateHistory; the Web app only joins that table at chart-render time.
builder.Services.AddHttpClient<IFiatRateHistoryService, FiatRateHistoryService>();

// ChangeNOW affiliate "trade for Monero" links. The singleton holds a periodically
// refreshed snapshot of ChangeNOW's supported currencies; the warmer keeps it fresh
// and off the request path. AddHttpClient() ensures IHttpClientFactory is available
// (also used by the sponsor proxy endpoint).
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IChangeNowLinkService, ChangeNowLinkService>();

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
// Currency-aware: for USD requests, returns CoinPriceHistories straight. For
// any other supported currency, joins each day's USD price against the FX rate
// from FiatRateHistory for that exact date — so chart shapes actually differ
// between currencies (CAD/USD movement over time is visible in the CAD chart,
// not flattened out by today's-rate-only conversion).
//
// Fallback chain when a specific date isn't in FiatRateHistory:
//   1. Backward walk up to 14 days within FiatRateHistory (weekends/holidays).
//   2. Today's live rate from IFiatRateService — used when the currency isn't
//      tracked at all (e.g. RUB, ARS — ECB doesn't publish via frankfurter).
//      This keeps the chart's absolute values consistent with the rest of the
//      page (current price, market cap, etc.) instead of collapsing to USD.
//   3. 1.0 as the ultimate safety net (only hits if both above are unavailable).
//
// Cache key includes the currency so we don't cross-contaminate.
app.MapGet("/api/coin/{coinGeckoId}/chart", async (
    string coinGeckoId,
    string? currency,
    IServiceScopeFactory scopeFactory,
    IFiatRateHistoryService fxHistory,
    IFiatRateService fxRates,
    IMemoryCache cache) =>
{
    // Normalize and validate the currency. Anything we don't display → USD.
    var currencyCode = (currency ?? "USD").Trim().ToUpperInvariant();
    if (!CurrencyCatalog.IsSupported(currencyCode))
        currencyCode = "USD";

    var cacheKey = $"chart_{coinGeckoId}_{currencyCode}";
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

    // Date → rate map for the requested currency. USD short-circuits inside
    // the service to a synthetic all-1.0 map (no DB hit), so the USD path
    // stays as fast as before.
    var fxMap = await fxHistory.GetSeriesAsync(
        currencyCode, cutoff, DateTime.UtcNow.Date);

    // Today's live rate, used as the constant fallback when FiatRateHistory has
    // nothing for this currency (RUB/ARS) or when a single date plus its 14-day
    // backward window all happen to be missing. Without this, the chart would
    // multiply USD prices by 1.0 and label them with the wrong currency symbol.
    var liveRates = await fxRates.GetRatesAsync();
    var liveFallbackRate = liveRates.TryGetValue(currencyCode, out var lr) && lr > 0 ? lr : 1m;

    var result = history
        .Select(x =>
        {
            // Look up that exact day's FX rate. If the row is missing — which
            // shouldn't happen for tracked currencies because the worker
            // forward-fills weekends on insert — walk backward up to 14 days
            // to find the nearest preceding rate. Final fallback is today's
            // live rate so the chart stays on the right scale.
            if (!fxMap.TryGetValue(x.Date, out var fx))
            {
                fx = 0m;
                for (int back = 1; back <= 14; back++)
                {
                    if (fxMap.TryGetValue(x.Date.AddDays(-back), out var prev))
                    {
                        fx = prev;
                        break;
                    }
                }
                if (fx == 0m) fx = liveFallbackRate;
            }

            return new double[]
            {
                new DateTimeOffset(x.Date, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                (double)(x.Price * fx)
            };
        })
        .ToList();

    var json = System.Text.Json.JsonSerializer.Serialize(result);
    cache.Set(cacheKey, json, TimeSpan.FromHours(1));
    return Results.Content(json, "application/json");
});

// ── 24h intraday chart (/api/coin/{id}/chart24h) ─────────────────────────────
// Served entirely from the DB: the worker writes a "5m" price snapshot per coin
// each refresh cycle, so we just read the last 24h of those rows. No live
// CoinGecko call from the web box (that path is unreliable / can't reach the
// API). Prices are USD; apply today's live FX uniformly — within a day the rate
// barely moves, so per-point FX history isn't worth it. Cached 2 min.
app.MapGet("/api/coin/{coinGeckoId}/chart24h", async (
    string coinGeckoId,
    string? currency,
    IServiceScopeFactory scopeFactory,
    IFiatRateService fxRates,
    IMemoryCache cache) =>
{
    var currencyCode = (currency ?? "USD").Trim().ToUpperInvariant();
    if (!CurrencyCatalog.IsSupported(currencyCode))
        currencyCode = "USD";

    var cacheKey = $"chart24h_{coinGeckoId}_{currencyCode}";
    if (cache.TryGetValue(cacheKey, out string? cached))
        return Results.Content(cached!, "application/json");

    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    var cutoff = DateTime.UtcNow.AddHours(-24);

    var snapshots = await db.CoinPriceHistories
        .Where(h => h.Coin.CoinGeckoId == coinGeckoId
                 && h.Interval == "5m"
                 && h.RecordedAt >= cutoff)
        .OrderBy(h => h.RecordedAt)
        .Select(h => new { h.RecordedAt, h.PriceUsd })
        .ToListAsync();

    // No intraday history yet (worker hasn't run since deploy, or coin is new).
    // 204 lets the front end say "collecting…" rather than show a broken chart.
    if (snapshots.Count == 0)
        return Results.StatusCode(204);

    var liveRates = await fxRates.GetRatesAsync();
    var fx = currencyCode == "USD"
        ? 1m
        : (liveRates.TryGetValue(currencyCode, out var lr) && lr > 0 ? lr : 1m);

    var result = snapshots
        .Select(s => new double[]
        {
            new DateTimeOffset(s.RecordedAt, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            (double)(s.PriceUsd * fx)
        })
        .ToList();

    var json = System.Text.Json.JsonSerializer.Serialize(result);
    cache.Set(cacheKey, json, TimeSpan.FromMinutes(2));
    return Results.Content(json, "application/json");
});

// ── Sponsor proxy (/api/sponsors) ────────────────────────────────────────────
var _sponsorCache = string.Empty;
var _sponsorCachedAt = DateTime.MinValue;
var _sponsorCacheTtl = TimeSpan.FromMinutes(
    builder.Configuration.GetValue<int>("Sponsors:CacheTtlMinutes", 5));
var _sponsorLock = new SemaphoreSlim(1, 1);

// ── ChangeNOW link diagnostic (/api/changenow/status) — TEMP, remove later ───
app.MapGet("/api/changenow/status", (IChangeNowLinkService changeNow) =>
    Results.Json(new
    {
        enabled = changeNow.Enabled,
        refreshIntervalHours = changeNow.RefreshInterval.TotalHours,
        resolve = new
        {
            sky = changeNow.ResolveFromTicker("sky"),
            btc = changeNow.ResolveFromTicker("btc"),
            eth = changeNow.ResolveFromTicker("eth"),
        },
        skyUrl = changeNow.ResolveTradeUrl("sky"),
    }));

 

// ── Sitemap (/sitemap.xml) ───────────────────────────────────────────────────
app.MapGet("/sitemap.xml", async (ICoinRepository coins) =>
{
    const string baseUrl = "https://moneromarketcap.com";
    const int topCoinCount = 100;

    var all = await coins.GetAllAsync();
    var topCoins = all
        .Where(c => !string.IsNullOrWhiteSpace(c.Symbol) && c.MarketCapUsd > 0)
        .OrderByDescending(c => c.MarketCapUsd)
        .Take(topCoinCount)
        .ToList();

    var lastMod = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
    var sb = new System.Text.StringBuilder();

    sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
    sb.AppendLine("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">");

    sb.AppendLine("  <url>");
    sb.AppendLine($"    <loc>{baseUrl}/</loc>");
    sb.AppendLine($"    <lastmod>{lastMod}</lastmod>");
    sb.AppendLine("    <changefreq>hourly</changefreq>");
    sb.AppendLine("    <priority>1.0</priority>");
    sb.AppendLine("  </url>");

    foreach (var coin in topCoins)
    {
        var slug = coin.Symbol.Trim().ToLowerInvariant();
        sb.AppendLine("  <url>");
        sb.AppendLine($"    <loc>{baseUrl}/coins/{slug}</loc>");
        sb.AppendLine($"    <lastmod>{lastMod}</lastmod>");
        sb.AppendLine("    <changefreq>hourly</changefreq>");
        sb.AppendLine("    <priority>0.8</priority>");
        sb.AppendLine("  </url>");
    }

    sb.AppendLine("</urlset>");
    return Results.Content(sb.ToString(), "application/xml");
});

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

// Route 404s (and other error codes) to the NotFound page
app.UseStatusCodePagesWithReExecute("/NotFound");

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