using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Services.Interfaces;

namespace MoneroMarketCap.Worker;

/// <summary>
/// Weekly reconciliation of the Exchange / ExchangeCoin / ExchangeContact tables against
/// SwapRaven's /api/exchanges catalog. Upserts each exchange by slug (details, contacts,
/// supported coins) and removes any no longer in the catalog. Runs once shortly after
/// startup (to preload) and then every SwapRaven:SyncIntervalDays (default 7). If the fetch
/// fails or returns empty, existing data is left untouched.
/// </summary>
public class SwapRavenExchangeSyncWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SwapRavenExchangeSyncWorker> _logger;
    private readonly IConfiguration _config;

    public SwapRavenExchangeSyncWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<SwapRavenExchangeSyncWorker> logger,
        IConfiguration config)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SwapRavenExchangeSyncWorker starting");

        var days = _config.GetValue<int>("SwapRaven:SyncIntervalDays", 7);
        var interval = TimeSpan.FromDays(days < 1 ? 7 : days);

        // Let the coin list settle after boot before the first (preload) run.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
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
                _logger.LogError(ex, "SwapRaven exchange sync cycle failed");
            }

            _logger.LogInformation("Next SwapRaven exchange sync in {Days} day(s)", days);
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
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var swapraven = scope.ServiceProvider.GetRequiredService<ISwapRavenClient>();

        IReadOnlyList<SwapRavenCatalogExchangeDto> catalog;
        try
        {
            catalog = await swapraven.GetCatalogAsync(ct);
        }
        catch
        {
            _logger.LogWarning("SwapRaven catalog fetch failed — leaving exchange data unchanged");
            return;
        }

        if (catalog.Count == 0)
        {
            _logger.LogWarning("SwapRaven catalog returned empty — leaving exchange data unchanged");
            return;
        }

        // Ticker -> MMC coin id (case-insensitive; last wins on any dup).
        var coinRows = await db.Coins.AsNoTracking().Select(c => new { c.Id, c.Symbol }).ToListAsync(ct);
        var coinIdByTicker = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in coinRows)
        {
            if (!string.IsNullOrWhiteSpace(c.Symbol))
            {
                coinIdByTicker[c.Symbol] = c.Id;
            }
        }

        // Only reconcile SwapRaven-sourced rows; CEX exchanges (Kraken/Coinbase) are
        // managed by their own worker and must never be removed here.
        var existing = await db.Exchanges
            .Where(e => e.Source == "SwapRaven")
            .Include(e => e.ExchangeCoins)
            .Include(e => e.Contacts)
            .ToListAsync(ct);
        var bySlug = new Dictionary<string, Exchange>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in existing)
        {
            bySlug[e.Slug] = e;
        }

        var now = DateTime.UtcNow;
        var seenSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int added = 0, updated = 0, order = 0;

        foreach (var dto in catalog)
        {
            if (string.IsNullOrWhiteSpace(dto.Slug))
            {
                continue;
            }

            seenSlugs.Add(dto.Slug);

            if (!bySlug.TryGetValue(dto.Slug, out var ex))
            {
                ex = new Exchange { Slug = dto.Slug };
                db.Exchanges.Add(ex);
                added++;
            }
            else
            {
                updated++;
            }

            ex.Name = dto.Name;
            ex.Kind = dto.Kind;
            ex.Description = dto.Description;
            ex.WebsiteUrl = dto.WebsiteUrl ?? string.Empty;
            ex.AffiliateUrl = dto.AffiliateUrl;
            ex.TorUrl = dto.TorUrl;
            ex.I2pUrl = dto.I2pUrl;
            ex.CountryCode = dto.CountryCode;
            ex.Grade = dto.Grade;
            ex.Kyc = dto.Kyc;
            ex.Aml = dto.Aml;
            ex.FeeMinPercent = dto.FeeMinPercent;
            ex.FeeMaxPercent = dto.FeeMaxPercent;
            ex.FeeVariesByProvider = dto.FeeVariesByProvider;
            ex.ProcessingTimeMinutes = dto.ProcessingTimeMinutes;
            ex.LaunchedAtUtc = dto.LaunchedAtUtc;
            ex.SortOrder = order++;
            ex.UpdatedAt = now;

            // Coins: diff against existing so the (ExchangeId, CoinId) unique index is never
            // re-inserted for a row that already exists.
            var wanted = new HashSet<int>();
            foreach (var ticker in dto.Coins)
            {
                if (!string.IsNullOrWhiteSpace(ticker) && coinIdByTicker.TryGetValue(ticker, out var cid))
                {
                    wanted.Add(cid);
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

            // Contacts: full replace (no unique constraint to conflict with).
            db.ExchangeContacts.RemoveRange(ex.Contacts);
            ex.Contacts.Clear();
            foreach (var c in dto.Contacts)
            {
                if (!string.IsNullOrWhiteSpace(c.Value))
                {
                    ex.Contacts.Add(new ExchangeContact { Type = c.Type ?? string.Empty, Value = c.Value, Label = c.Label });
                }
            }
        }

        int removed = 0;
        foreach (var ex in existing)
        {
            if (!seenSlugs.Contains(ex.Slug))
            {
                db.Exchanges.Remove(ex);
                removed++;
            }
        }

        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "SwapRaven catalog sync done: {Total} exchanges in catalog (+{Added} ~{Updated} -{Removed})",
            catalog.Count, added, updated, removed);
    }
}
