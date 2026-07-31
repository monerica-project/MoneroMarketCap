using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Models;

namespace MoneroMarketCap.Worker;

/// <summary>
/// Syncs the centralized exchanges we hold affiliate links for — Kraken and Coinbase —
/// into the Exchange / ExchangeCoin tables (Source = "Cex"). Each one's supported-coin list
/// comes from its PUBLIC coin API; we link only the coins MoneroMarketCap already tracks.
/// Static details (name, website, affiliate link) live here. Runs shortly after startup and
/// then every Cex:SyncIntervalDays (default 7). A failed fetch leaves existing rows untouched,
/// and the SwapRaven sync never removes these (it is scoped to Source = "SwapRaven").
/// </summary>
public class CexExchangeSyncWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<CexExchangeSyncWorker> _logger;
    private readonly IConfiguration _config;

    public CexExchangeSyncWorker(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpFactory,
        ILogger<CexExchangeSyncWorker> logger,
        IConfiguration config)
    {
        _scopeFactory = scopeFactory;
        _httpFactory = httpFactory;
        _logger = logger;
        _config = config;
    }

    private sealed record CexDef(string Slug, string Name, string Website, string Affiliate, string Description, int SortOrder);

    private static readonly CexDef Kraken = new(
        "kraken", "Kraken", "https://www.kraken.com",
        "https://kraken.pxf.io/c/1197953/741638/10583",
        "Kraken is a large, long-established centralized exchange offering spot and futures trading and staking. Creating an account and completing identity verification (KYC) is required.",
        900);

    private static readonly CexDef Coinbase = new(
        "coinbase", "Coinbase", "https://www.coinbase.com",
        "https://coinbase.com/join/FUCF9E4?src=referral-link",
        "Coinbase is one of the largest centralized exchanges, widely used for buying crypto with cards and bank transfers. Creating an account and completing identity verification (KYC) is required.",
        901);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CexExchangeSyncWorker starting");
        var days = _config.GetValue<int>("Cex:SyncIntervalDays", 7);
        var interval = TimeSpan.FromDays(days < 1 ? 7 : days);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CEX exchange sync cycle failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SyncAsync(CancellationToken ct)
    {
        var http = _httpFactory.CreateClient("cex");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Deterministic ticker -> coin id (shared with the SwapRaven sync) so a shared
        // symbol resolves to the same coin in both workers. See CoinTickerMap.
        var coinIdByTicker = await CoinTickerMap.BuildAsync(db, ct);

        await SyncOneAsync(db, coinIdByTicker, Kraken, await SafeFetch(() => FetchKrakenSymbolsAsync(http, ct), Kraken.Name), ct);
        await SyncOneAsync(db, coinIdByTicker, Coinbase, await SafeFetch(() => FetchCoinbaseSymbolsAsync(http, ct), Coinbase.Name), ct);
    }

    private async Task<HashSet<string>?> SafeFetch(Func<Task<HashSet<string>>> fetch, string name)
    {
        try
        {
            var set = await fetch();
            if (set.Count == 0)
            {
                _logger.LogWarning("CEX {Name} returned no coins — leaving rows unchanged", name);
                return null;
            }

            return set;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CEX coin fetch failed for {Name} — leaving rows unchanged", name);
            return null;
        }
    }

    private async Task SyncOneAsync(AppDbContext db, Dictionary<string, int> coinIdByTicker, CexDef def, HashSet<string>? symbols, CancellationToken ct)
    {
        if (symbols is null)
        {
            return;
        }

        var ex = await db.Exchanges.Include(e => e.ExchangeCoins).FirstOrDefaultAsync(e => e.Slug == def.Slug, ct);
        if (ex is null)
        {
            ex = new Exchange { Slug = def.Slug };
            db.Exchanges.Add(ex);
        }

        ex.Source = "Cex";
        ex.Name = def.Name;
        ex.Kind = "CEX";
        ex.Description = def.Description;
        ex.WebsiteUrl = def.Website;
        ex.AffiliateUrl = def.Affiliate;
        ex.Kyc = "Required";
        ex.Aml = "Enforced";
        ex.Grade = null;
        ex.FeeVariesByProvider = false;
        ex.SortOrder = def.SortOrder;
        ex.UpdatedAt = DateTime.UtcNow;

        var wanted = new HashSet<int>();
        foreach (var s in symbols)
        {
            if (coinIdByTicker.TryGetValue(s, out var id))
            {
                wanted.Add(id);
            }
        }

        var have = ex.ExchangeCoins.Select(x => x.CoinId).ToHashSet();
        foreach (var xc in ex.ExchangeCoins.Where(x => !wanted.Contains(x.CoinId)).ToList())
        {
            db.ExchangeCoins.Remove(xc);
            ex.ExchangeCoins.Remove(xc);
        }

        foreach (var cid in wanted.Where(id => !have.Contains(id)))
        {
            ex.ExchangeCoins.Add(new ExchangeCoin { CoinId = cid });
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("CEX sync {Name}: {ApiCoins} coins from API, {Linked} matched MMC coins", def.Name, symbols.Count, wanted.Count);
    }

    private static async Task<HashSet<string>> FetchKrakenSymbolsAsync(HttpClient http, CancellationToken ct)
    {
        using var resp = await http.GetAsync("https://api.kraken.com/0/public/Assets", ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("result", out var result))
        {
            foreach (var prop in result.EnumerateObject())
            {
                var altname = prop.Value.TryGetProperty("altname", out var a) ? a.GetString() : prop.Name;
                var sym = NormalizeKraken(altname);
                if (sym is not null)
                {
                    set.Add(sym);
                }
            }
        }

        return set;
    }

    private static string? NormalizeKraken(string? altname)
    {
        if (string.IsNullOrWhiteSpace(altname))
        {
            return null;
        }

        var s = altname.Trim().ToUpperInvariant();

        // Skip staking / earn variants like "ETH2.S", "DOT.S".
        if (s.Contains('.'))
        {
            return null;
        }

        return s switch
        {
            "XBT" => "BTC",
            "XDG" => "DOGE",
            _ => s,
        };
    }

    private static async Task<HashSet<string>> FetchCoinbaseSymbolsAsync(HttpClient http, CancellationToken ct)
    {
        using var resp = await http.GetAsync("https://api.exchange.coinbase.com/currencies", ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var type = el.TryGetProperty("details", out var d) && d.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (!string.Equals(type, "crypto", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (!string.IsNullOrWhiteSpace(id))
            {
                set.Add(id.Trim().ToUpperInvariant());
            }
        }

        return set;
    }
}
