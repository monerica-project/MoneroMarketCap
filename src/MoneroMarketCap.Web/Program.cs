using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Constants;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Data.Repositories;
using MoneroMarketCap.Services.Implementations;
using MoneroMarketCap.Services.Interfaces;
using MoneroMarketCap.Services.Models;
using MoneroMarketCap.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        b => b.MigrationsAssembly("MoneroMarketCap.Data")));

builder.Services.AddRazorPages(options =>
{
    // Clean path-based pagination for the coin page's exchange table:
    // /coins/{symbol}/exchanges/{page}. Page 1 stays the plain /coins/{symbol}.
    options.Conventions.AddPageRoute("/Coins/CoinDetail", "coins/{symbol}/exchanges/{xp:int}");

    // Defense-in-depth: require authentication for the ENTIRE /Portfolios folder (private
    // holdings + transactions) and the AdminOnly policy for the ENTIRE /Admin folder, so a
    // new page added under either can never be accidentally left anonymous. This is on top of
    // the per-page [Authorize]/[Authorize(Policy="AdminOnly")] already on each page. Public
    // pages (coins, exchanges, login, etc.) are unaffected — only these folders are gated.
    options.Conventions.AuthorizeFolder("/Portfolios");
    options.Conventions.AuthorizeFolder("/Admin", "AdminOnly");
});

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

// ChangeNOW affiliate "trade for Monero" links. The Web only BUILDS a link from a
// coin's already-resolved ChangeNowTicker + the config template — no fetch, no cache,
// no background work here (resolution lives in the Worker). AddHttpClient() stays for
// the sponsor proxy; this singleton never makes a network call in this process.
builder.Services.AddHttpClient();

// Cached Monerica sponsor feed — used by /api/sponsors AND server-rendered so no-JS visitors see sponsors.
builder.Services.AddSingleton<ISponsorFeed, SponsorFeed>();
builder.Services.AddSingleton<IChangeNowLinkService, ChangeNowLinkService>();

builder.Services.AddAuthentication("CookieAuth")
    .AddCookie("CookieAuth", options =>
    {
        options.LoginPath = "/Login";
        options.LogoutPath = "/Logout";

        // Harden the auth cookie so a stolen/observed cookie can't be used to read a
        // user's portfolio: not readable from JavaScript (blunts XSS theft), only sent
        // over HTTPS, and Lax same-site to limit cross-site sends.
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
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

// ── Never cache error responses ────────────────────────────────────────────────
// Brave (and other browsers) on Windows were serving stale cached 404/500 pages:
// once a URL 404'd (e.g. a coin page before that coin existed) or hit a transient
// 500, the browser kept showing the cached error even after the page became valid.
// Error responses carry no Cache-Control by default, so browsers fall back to
// heuristic caching. Force no-store on anything 4xx/5xx so every error is always
// refetched. Registered FIRST so its OnStarting callback sees the final status code,
// including the /NotFound and /Error pages re-executed by the middleware below.
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        if (context.Response.StatusCode >= 400)
        {
            context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
            context.Response.Headers["Pragma"] = "no-cache";
            context.Response.Headers["Expires"] = "0";
            context.Response.Headers.Remove("ETag");
            context.Response.Headers.Remove("Last-Modified");
        }
        return Task.CompletedTask;
    });

    await next();
});

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

    // We display ~1 year, but also return up to 200 extra days of older history so
    // the client can seed moving averages (SMA 200, etc.) from the first visible
    // day rather than only 200 points in. The client slices the display back to a
    // year; these older rows feed the indicator math only. When a coin has less
    // than a year+warm-up of history, this simply returns whatever exists.
    const int visibleDays = 364;
    const int indicatorWarmupDays = 200;
    var cutoff = DateTime.UtcNow.Date.AddDays(-(visibleDays + indicatorWarmupDays));

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
    HttpContext ctx,
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

    // The 5-minute snapshots only accumulate while the worker runs, so right after
    // a deploy (or on a brand-new coin) there are few/none. Rather than show
    // "unavailable", fall back to the hourly ("1h") series for the last 24h — it's
    // maintained in the background and gives ~24 points. As 5m snapshots build up
    // they take over automatically (richer line). Threshold: ~1h of 5m data.
    if (snapshots.Count < 12)
    {
        var hourly = await db.CoinPriceHistories
            .Where(h => h.Coin.CoinGeckoId == coinGeckoId
                     && h.Interval == "1h"
                     && h.RecordedAt >= cutoff)
            .OrderBy(h => h.RecordedAt)
            .Select(h => new { h.RecordedAt, h.PriceUsd })
            .ToListAsync();
        if (hourly.Count > snapshots.Count)
            snapshots = hourly;
    }

    // Still nothing — no intraday or hourly history yet. 204 lets the front end say
    // "collecting…" rather than show a broken chart, and must NOT be cached so the
    // chart fills in once the worker produces data.
    if (snapshots.Count == 0)
    {
        ctx.Response.Headers["Cache-Control"] = "no-store";
        return Results.StatusCode(204);
    }

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

// ── Fine (hourly) chart for short ranges (/api/coin/{id}/chartfine?days=7|30) ──
// The DB only stores one point per day (1d) plus ~48h of 5-minute snapshots, so
// 7D/30D would otherwise draw a sparse 7- or 30-point line. We keep a persistent
// hourly ("1h") series in the DB so the chart is ALWAYS available and consistent —
// not dependent on reaching CoinGecko at request time. The series is (re)built from
// CoinGecko's hourly data only when the DB copy is missing or stale (>90 min old);
// every request thereafter is served from the DB. USD prices are stored once and
// converted to the requested currency on read using the same per-day FiatRateHistory
// rates as the daily chart. Returns 204 only when there's no DB data AND CoinGecko
// is unreachable, so the client can fall back to the daily slice.
app.MapGet("/api/coin/{coinGeckoId}/chartfine", async (
    HttpContext ctx,
    string coinGeckoId,
    int days,
    string? currency,
    IServiceScopeFactory scopeFactory,
    IFiatRateHistoryService fxHistory,
    IFiatRateService fxRates,
    IMemoryCache cache) =>
{
    var currencyCode = (currency ?? "USD").Trim().ToUpperInvariant();
    if (!CurrencyCatalog.IsSupported(currencyCode))
        currencyCode = "USD";

    // Only the short ranges that benefit from hourly data.
    var windowDays = days <= 7 ? 7 : 30;

    var cacheKey = $"chartfine_{coinGeckoId}_{currencyCode}_{windowDays}";
    if (cache.TryGetValue(cacheKey, out string? cached))
        return Results.Content(cached!, "application/json");

    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    var coin = await db.Coins
        .Where(c => c.CoinGeckoId == coinGeckoId)
        .Select(c => new { c.Id })
        .FirstOrDefaultAsync();

    if (coin is null)
    {
        ctx.Response.Headers["Cache-Control"] = "no-store";
        return Results.StatusCode(204);
    }

    // Served entirely from the DB — the Worker's HourlyHistoryBackfillService keeps
    // the "1h" series fresh in the background, so this endpoint never calls CoinGecko
    // and returns instantly. If the worker hasn't populated this coin yet, return 204
    // so the client falls back to the daily slice until the hourly data arrives.
    var windowStart = DateTime.UtcNow.AddDays(-windowDays);
    var rows = await db.CoinPriceHistories
        .Where(h => h.CoinId == coin.Id && h.Interval == "1h" && h.RecordedAt >= windowStart)
        .OrderBy(h => h.RecordedAt)
        .Select(h => new { h.RecordedAt, h.PriceUsd })
        .ToListAsync();

    if (rows.Count == 0)
    {
        // No hourly data and we couldn't fetch any — let the client use the daily slice.
        ctx.Response.Headers["Cache-Control"] = "no-store";
        return Results.StatusCode(204);
    }

    var fxCutoff = DateTime.UtcNow.Date.AddDays(-(windowDays + 2));
    var fxMap = await fxHistory.GetSeriesAsync(currencyCode, fxCutoff, DateTime.UtcNow.Date);
    var liveRates = await fxRates.GetRatesAsync();
    var liveFallbackRate = liveRates.TryGetValue(currencyCode, out var lr) && lr > 0 ? lr : 1m;

    var result = rows
        .Select(r =>
        {
            var date = r.RecordedAt.Date;
            // Same fx lookup as the daily chart: exact day, else walk back 14 days,
            // else today's live rate. USD short-circuits to an all-1.0 map.
            if (!fxMap.TryGetValue(date, out var fx))
            {
                fx = 0m;
                for (int back = 1; back <= 14; back++)
                {
                    if (fxMap.TryGetValue(date.AddDays(-back), out var prev)) { fx = prev; break; }
                }
                if (fx == 0m) fx = liveFallbackRate;
            }
            return new double[]
            {
                new DateTimeOffset(r.RecordedAt, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                (double)(r.PriceUsd * fx)
            };
        })
        .ToList();

    var json = System.Text.Json.JsonSerializer.Serialize(result);
    cache.Set(cacheKey, json, TimeSpan.FromMinutes(15));
    return Results.Content(json, "application/json");
});

// ── Sponsor proxy (/api/sponsors) — cache now lives in ISponsorFeed ──────────

// ── Spot price by ticker (/api/price/{symbol}) ───────────────────────────────
// Returns the latest fiat price for a coin by its ticker symbol. The price is
// read from the Coins table (kept fresh by the Worker's CoinPriceUpdateService),
// so this endpoint never touches CoinGecko and carries no upstream rate-limit risk.
//
//   GET /api/price/btc            → USD price
//   GET /api/price/btc?vs=eur     → EUR price (any CurrencyCatalog code; USD default)
//
// Symbols are stored uppercase and are not guaranteed unique across listed assets,
// so the canonical match is the active coin with the best (lowest non-zero) market
// cap rank. Responses are cached briefly server-side and via Cache-Control.
app.MapGet("/api/price/{symbol}", async (
    string symbol,
    string? vs,
    HttpContext ctx,
    IServiceScopeFactory scopeFactory,
    IMemoryCache cache) =>
{
    var ticker = (symbol ?? string.Empty).Trim().ToUpperInvariant();
    if (ticker.Length is 0 or > 32)
        return Results.Json(new { error = "Invalid ticker.", symbol }, statusCode: 400);

    // Resolve the display currency. Anything we don't support falls back to USD.
    var currency = (vs ?? CurrencyCatalog.DefaultCode).Trim().ToUpperInvariant();
    if (!CurrencyCatalog.IsSupported(currency))
        currency = CurrencyCatalog.DefaultCode;

    ctx.Response.Headers["Cache-Control"] = "public, max-age=30";

    var cacheKey = $"price_{ticker}_{currency}";
    if (cache.TryGetValue(cacheKey, out IResult? cached) && cached is not null)
        return cached;

    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    // Canonical coin for this ticker: prefer active, then the best-ranked
    // (rank 0 = unranked, sorted last), then highest market cap as a tiebreak.
    var coin = await db.Coins
        .Where(c => c.Symbol == ticker)
        .OrderByDescending(c => c.IsActive)
        .ThenBy(c => c.MarketCapRank == 0)
        .ThenBy(c => c.MarketCapRank)
        .ThenByDescending(c => c.MarketCapUsd)
        .Select(c => new
        {
            c.Symbol,
            c.Name,
            c.PriceUsd,
            c.MarketCapRank,
            c.UpdatedAt
        })
        .FirstOrDefaultAsync();

    if (coin is null)
        return Results.Json(new { error = "Unknown ticker.", symbol = ticker }, statusCode: 404);

    // Convert USD → requested currency. GetRatesAsync returns units-per-USD
    // (USD => 1.0); USD short-circuits with no DB hit.
    var rate = 1m;
    if (currency != CurrencyCatalog.DefaultCode)
    {
        var fx = scope.ServiceProvider.GetRequiredService<IFiatRateService>();
        var rates = await fx.GetRatesAsync(ctx.RequestAborted);
        if (!rates.TryGetValue(currency, out rate) || rate <= 0)
        {
            // Rate unavailable (worker hasn't synced this currency yet) → USD.
            currency = CurrencyCatalog.DefaultCode;
            rate = 1m;
        }
    }

    var result = Results.Json(new
    {
        symbol = coin.Symbol,
        name = coin.Name,
        currency,
        price = coin.PriceUsd * rate,
        priceUsd = coin.PriceUsd,
        rate,
        marketCapRank = coin.MarketCapRank,
        lastUpdated = coin.UpdatedAt
    });

    cache.Set(cacheKey, result, TimeSpan.FromSeconds(30));
    return result;
});

// ── Sitemaps (index + child sitemaps: pages, exchanges, coins) ───────────────
// /sitemap.xml is a sitemap *index* pointing at child sitemaps, so the directory
// can grow without hitting the 50,000-URL / 50MB per-file limits. Coin detail
// pages are chunked across /sitemap-coins-{n}.xml; exchanges and static/evergreen
// pages get their own files.
const string sitemapBaseUrl = "https://moneromarketcap.com";
const int sitemapCoinChunk = 20000;

// Ordered, de-duplicated coin slugs (highest market cap first) — one URL per symbol.
static List<string> SitemapCoinSlugs(IReadOnlyList<Coin> all)
{
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var slugs = new List<string>();
    foreach (var c in all
        .Where(c => c.IsActive && !string.IsNullOrWhiteSpace(c.Symbol))
        .OrderByDescending(c => c.MarketCapUsd)
        .ThenBy(c => c.Symbol, StringComparer.OrdinalIgnoreCase))
    {
        var slug = MoneroMarketCap.Web.Helpers.CoinRoute.Key(c);
        if (seen.Add(slug))
        {
            slugs.Add(slug);
        }
    }

    return slugs;
}

static string SitemapXmlEscape(string s) => s
    .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
    .Replace("\"", "&quot;").Replace("'", "&apos;");

static string BuildUrlSet(IEnumerable<(string Loc, string LastMod, string ChangeFreq, string Priority)> urls)
{
    var sb = new System.Text.StringBuilder();
    sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
    sb.AppendLine("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">");
    foreach (var (loc, lastMod, changeFreq, priority) in urls)
    {
        sb.AppendLine("  <url>");
        sb.AppendLine($"    <loc>{loc}</loc>");
        sb.AppendLine($"    <lastmod>{lastMod}</lastmod>");
        sb.AppendLine($"    <changefreq>{changeFreq}</changefreq>");
        sb.AppendLine($"    <priority>{priority}</priority>");
        sb.AppendLine("  </url>");
    }

    sb.AppendLine("</urlset>");
    return sb.ToString();
}

// Sitemap index
app.MapGet("/sitemap.xml", async (ICoinRepository coins) =>
{
    var all = await coins.GetAllAsync();
    var coinCount = SitemapCoinSlugs(all).Count;
    var coinPages = Math.Max(1, (int)Math.Ceiling(coinCount / (double)sitemapCoinChunk));
    var lastMod = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

    var sb = new System.Text.StringBuilder();
    sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
    sb.AppendLine("<sitemapindex xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">");

    void Child(string path)
    {
        sb.AppendLine("  <sitemap>");
        sb.AppendLine($"    <loc>{sitemapBaseUrl}{path}</loc>");
        sb.AppendLine($"    <lastmod>{lastMod}</lastmod>");
        sb.AppendLine("  </sitemap>");
    }

    Child("/sitemap-pages.xml");
    Child("/sitemap-exchanges.xml");
    for (var p = 1; p <= coinPages; p++)
    {
        Child($"/sitemap-coins-{p}.xml");
    }

    sb.AppendLine("</sitemapindex>");
    return Results.Content(sb.ToString(), "application/xml");
});

// Static / evergreen pages
app.MapGet("/sitemap-pages.xml", () =>
{
    var lastMod = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
    var pages = new (string Path, string ChangeFreq, string Priority)[]
    {
        ("/", "hourly", "1.0"),
        ("/coins", "hourly", "0.8"),
        ("/exchanges", "daily", "0.9"),
        ("/vs-monero", "daily", "0.9"),
        ("/network", "daily", "0.7"),
        ("/sponsors", "weekly", "0.5"),
        ("/about", "monthly", "0.4"),
        ("/contact", "monthly", "0.3"),
        ("/privacy", "yearly", "0.2"),
    };

    var urls = pages.Select(p => ($"{sitemapBaseUrl}{p.Path}", lastMod, p.ChangeFreq, p.Priority));
    return Results.Content(BuildUrlSet(urls), "application/xml");
});

// All exchange detail pages
app.MapGet("/sitemap-exchanges.xml", async (AppDbContext db) =>
{
    var exchanges = await db.Exchanges.AsNoTracking()
        .Where(e => e.Slug != "")
        .OrderBy(e => e.SortOrder).ThenBy(e => e.Name)
        .Select(e => new { e.Slug, e.UpdatedAt })
        .ToListAsync();

    var urls = exchanges.Select(e => (
        $"{sitemapBaseUrl}/exchanges/{SitemapXmlEscape(e.Slug)}",
        e.UpdatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        "weekly",
        "0.6"));

    return Results.Content(BuildUrlSet(urls), "application/xml");
});

// Coin detail pages (chunked)
app.MapGet("/sitemap-coins-{page:int}.xml", async (int page, ICoinRepository coins) =>
{
    if (page < 1)
    {
        return Results.NotFound();
    }

    var all = await coins.GetAllAsync();
    var chunk = SitemapCoinSlugs(all).Skip((page - 1) * sitemapCoinChunk).Take(sitemapCoinChunk).ToList();
    if (chunk.Count == 0)
    {
        return Results.NotFound();
    }

    var lastMod = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
    var urls = chunk.Select(s => ($"{sitemapBaseUrl}/coins/{SitemapXmlEscape(s)}", lastMod, "hourly", "0.8"));
    return Results.Content(BuildUrlSet(urls), "application/xml");
});

// robots.txt — point crawlers at the sitemap index
app.MapGet("/robots.txt", () => Results.Text(
    "User-agent: *\nAllow: /\n\nSitemap: https://moneromarketcap.com/sitemap.xml\n",
    "text/plain"));

app.MapGet("/api/sponsors", async (ISponsorFeed feed, HttpContext ctx, CancellationToken cancel) =>
{
    ctx.Response.Headers["Cache-Control"] = "public, max-age=1200";
    var json = await feed.GetRawJsonAsync(cancel);
    return Results.Content(string.IsNullOrEmpty(json) ? "[]" : json, "application/json");
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
        context.Response.StatusCode = 308;
        // Percent-encode so a non-ASCII path never produces an invalid (non-ASCII) header.
        context.Response.Headers.Location =
            new PathString(lower).ToUriComponent() + context.Request.QueryString.ToUriComponent();
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
                // Use the percent-encoded (ASCII) path/query — HTTP header values must be
                // ASCII, so a raw non-ASCII path (e.g. /coins/币安人生) would throw here.
                var onionUrl = $"http://{onionHost}{context.Request.Path.ToUriComponent()}{context.Request.QueryString.ToUriComponent()}";
                context.Response.Headers["Onion-Location"] = onionUrl;
            }
            return Task.CompletedTask;
        });
        await next();
    });
}

// Coin search autocomplete: matches by symbol or name, best matches first (exact
// symbol > symbol prefix > name prefix > contains), market-cap tiebroken, top 10.
app.MapGet("/api/coins/suggest", async (
    string? q,
    HttpContext ctx,
    IServiceScopeFactory scopeFactory) =>
{
    var term = (q ?? string.Empty).Trim();
    if (term.Length == 0)
    {
        return Results.Json(Array.Empty<object>());
    }

    ctx.Response.Headers["Cache-Control"] = "public, max-age=30";

    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    var like = $"%{term}%";
    var matches = await db.Coins
        .Where(c => EF.Functions.ILike(c.Symbol, like) || EF.Functions.ILike(c.Name, like))
        .OrderByDescending(c => c.IsActive)
        .ThenBy(c => c.MarketCapRank == 0)
        .ThenBy(c => c.MarketCapRank)
        .ThenByDescending(c => c.MarketCapUsd)
        .Select(c => new { c.Symbol, c.Name, c.ImageUrl, c.CoinGeckoId })
        .ToListAsync();

    // Match-quality rank, in memory (the filtered set is small). OrderBy is stable,
    // so the market-cap ordering above is preserved within each rank tier.
    int Rank(string sym, string name) =>
        string.Equals(sym, term, StringComparison.OrdinalIgnoreCase) ? 0
        : sym.StartsWith(term, StringComparison.OrdinalIgnoreCase) ? 1
        : name.StartsWith(term, StringComparison.OrdinalIgnoreCase) ? 2
        : 3;

    var results = matches
        .OrderBy(c => Rank(c.Symbol, c.Name))
        .Take(10)
        .Select(c => new
        {
            symbol = c.Symbol,
            name = c.Name,
            image = c.ImageUrl,
            url = $"/coins/{MoneroMarketCap.Web.Helpers.CoinRoute.Key(c.Symbol, c.CoinGeckoId)}",
        })
        .ToList();

    return Results.Json(results);
});

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// Throttled "last seen" tracker — must run after authentication so the user
// principal is populated. Updates AppUser.LastSeenAt at most once per user per
// few minutes; see LastSeenMiddleware.
app.UseMiddleware<MoneroMarketCap.Web.Middleware.LastSeenMiddleware>();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();