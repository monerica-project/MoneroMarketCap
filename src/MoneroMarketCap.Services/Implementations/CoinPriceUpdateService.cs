using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Helpers;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Services.Interfaces;
using MoneroMarketCap.Services.Models;

namespace MoneroMarketCap.Services.Implementations;

public class CoinPriceUpdateService : BackgroundService
{
    /// <summary>
    /// A coin that has already been backfilled is re-checked only this often, so a
    /// long outage can't leave permanent gaps. At ~300 coins this costs about one
    /// extra CoinGecko call per coin per month.
    /// </summary>
    private const int BackfillRefreshDays = 30;

    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<CoinPriceUpdateService> logger;
    private readonly CoinHistoryBackfillService backfillService;
    private readonly TimeSpan interval;
    private readonly int topCount;
    private readonly int backfillDelayMs;
    private readonly int backfillThresholdDays;

    public CoinPriceUpdateService(
        IServiceScopeFactory scopeFactory,
        ILogger<CoinPriceUpdateService> logger,
        CoinHistoryBackfillService backfillService,
        IConfiguration config)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
        this.backfillService = backfillService;
        var minutes = config.GetValue<int>("CoinGecko:RefreshIntervalMinutes", 8);
        this.interval = TimeSpan.FromMinutes(minutes);
        this.topCount = config.GetValue<int>("CoinGecko:TopCount", 100);
        this.backfillDelayMs = config.GetValue<int>("CoinGecko:BackfillDelayMs", 2500);
        this.backfillThresholdDays = config.GetValue<int>("CoinGecko:BackfillThresholdDays", 300);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            List<int> coinsNeedingBackfill = new();

            try
            {
                coinsNeedingBackfill = await UpdatePricesAsync();
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Price update cycle failed");
            }

            if (coinsNeedingBackfill.Count > 0)
            {
                try
                {
                    await BackfillCoinsAsync(coinsNeedingBackfill, stoppingToken);
                }
                catch (Exception ex)
                {
                    this.logger.LogError(ex, "Per-cycle backfill failed");
                }
            }

            await Task.Delay(this.interval, stoppingToken);
        }
    }

    /// <summary>
    /// Reconciles top N, upserts today's history. Returns the list of Coin IDs
    /// whose 1d history is below the backfill threshold and needs filling in.
    /// </summary>
    /// <remarks>
    /// Active-state rules (see <see cref="Coin.IsActiveOverride"/>):
    ///   * null override  → active exactly while in the top N (the old behaviour);
    ///   * true override  → always active; if outside the top N its price is fetched
    ///                      separately via the markets ids= query so it never goes stale;
    ///   * false override → never active, even while in the top N.
    /// Deactivation only happens on a COMPLETE top-N list. When CoinGecko fails part-way
    /// through paging, the coins that did arrive are still priced, but nothing is
    /// deactivated — a missing page is not evidence that coins left the top N.
    /// </remarks>
    internal async Task<List<int>> UpdatePricesAsync()
    {
        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var geckoService = scope.ServiceProvider.GetRequiredService<ICoinGeckoService>();

        var top = await geckoService.GetTopCoinsAsync(this.topCount);
        var topCoins = top?.Coins;
        if (topCoins == null || topCoins.Count == 0)
        {
            this.logger.LogWarning("CoinGecko returned no top coins; skipping this cycle");
            return new();
        }

        var isComplete = top!.IsComplete;
        if (!isComplete)
        {
            this.logger.LogWarning(
                "CoinGecko top-{Top} list is INCOMPLETE ({Got} received, a page failed): updating prices for the coins received but skipping deactivation this cycle",
                this.topCount, topCoins.Count);
        }

        var topIds = topCoins.Select(c => c.Id).ToHashSet();

        var existing = await db.Coins.ToListAsync();

        // Lowest Id wins if two rows ever share a CoinGecko id, rather than throwing.
        var existingById = existing
            .Where(c => !string.IsNullOrEmpty(c.CoinGeckoId))
            .GroupBy(c => c.CoinGeckoId)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Id).First());

        var now = DateTime.UtcNow;
        var today = now.Date;
        int added = 0, updated = 0, reactivated = 0, deactivated = 0, reEntered = 0, manual = 0;

        // Every coin whose market data was refreshed this cycle (top N + manual).
        var pricedThisCycle = new List<Coin>();

        foreach (var m in topCoins)
        {
            if (!existingById.TryGetValue(m.Id, out var coin))
            {
                coin = new Coin
                {
                    CoinGeckoId = m.Id,
                    Symbol = m.Symbol?.ToUpper() ?? "",
                    Name = m.Name ?? "",
                    CreatedAt = now,
                    LastInTopNUtc = now
                };
                db.Coins.Add(coin);
                existingById[m.Id] = coin;
                added++;
            }
            else
            {
                if (!coin.IsActive && coin.IsActiveOverride != false)
                {
                    reactivated++;

                    // Re-entry: the coin was out of the top N and is back. Whatever
                    // gap opened up while it was away is repaired by one market_chart
                    // call (CoinGecko:BackfillDays, up to the 2 years this plan
                    // allows), so clear the stamp and let the per-cycle selector
                    // pick it up instead of polling dropouts continuously.
                    coin.DailyHistoryBackfilledAtUtc = null;
                    reEntered++;
                }

                updated++;
            }

            coin.IsActive = coin.IsActiveOverride ?? true;
            coin.LastInTopNUtc = now;
            ApplyMarketData(coin, m, now);
            pricedThisCycle.Add(coin);
        }

        // Manually-activated coins outside the top N: the markets list never contains
        // them, so fetch their prices by id (one extra call per 250 such coins).
        var manualCoins = existing
            .Where(c => c.IsActiveOverride == true
                        && !string.IsNullOrEmpty(c.CoinGeckoId)
                        && !topIds.Contains(c.CoinGeckoId))
            .ToList();
        if (manualCoins.Count > 0)
        {
            var extra = await geckoService.GetMarketDataBatchAsync(manualCoins.Select(c => c.CoinGeckoId));
            foreach (var coin in manualCoins)
            {
                coin.IsActive = true;
                if (extra.TryGetValue(coin.CoinGeckoId, out var m))
                {
                    ApplyMarketData(coin, m, now);
                    pricedThisCycle.Add(coin);
                    manual++;
                }
            }
        }

        await db.SaveChangesAsync();

        foreach (var coin in existing)
        {
            if (string.IsNullOrEmpty(coin.CoinGeckoId)) continue;

            if (coin.IsActiveOverride == false)
            {
                if (coin.IsActive)
                {
                    coin.IsActive = false;
                    deactivated++;
                }

                continue;
            }

            if (isComplete
                && coin.IsActiveOverride != true
                && !topIds.Contains(coin.CoinGeckoId)
                && coin.IsActive)
            {
                coin.IsActive = false;
                deactivated++;
            }
        }

        var activeCoins = pricedThisCycle
            .Where(c => c.IsActive)
            .Distinct()
            .ToList();
        var activeIds = activeCoins.Select(c => c.Id).ToList();

        var todaysRows = await db.CoinPriceHistories
            .Where(h => h.Interval == "1d"
                     && h.RecordedAt == today
                     && activeIds.Contains(h.CoinId))
            .ToListAsync();

        // Tolerant of legacy duplicates (pre unique index): use the lowest-Id row per coin,
        // the same row the dedupe migration keeps.
        var todaysByCoinId = PickDailyRowPerCoin(todaysRows);

        int historyAdded = 0, historyUpdated = 0;

        foreach (var coin in activeCoins)
        {
            if (todaysByCoinId.TryGetValue(coin.Id, out var row))
            {
                row.PriceUsd = coin.PriceUsd;
                row.MarketCapUsd = coin.MarketCapUsd;
                row.CirculatingSupply = coin.CirculatingSupply;
                historyUpdated++;
            }
            else
            {
                db.CoinPriceHistories.Add(new CoinPriceHistory
                {
                    CoinId = coin.Id,
                    PriceUsd = coin.PriceUsd,
                    MarketCapUsd = coin.MarketCapUsd,
                    CirculatingSupply = coin.CirculatingSupply,
                    Interval = "1d",
                    RecordedAt = today
                });
                historyAdded++;
            }
        }

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            // Someone else wrote today's row between our read and insert (the unique
            // index on CoinId+Interval+RecordedAt caught it). Drop our pending inserts —
            // the next cycle updates the existing row — and keep the rest of the save.
            this.logger.LogWarning("Today's 1d row already existed for some coins; skipping those inserts this cycle");
            foreach (var entry in db.ChangeTracker.Entries<CoinPriceHistory>()
                         .Where(e => e.State == EntityState.Added)
                         .ToList())
            {
                entry.State = EntityState.Detached;
                historyAdded--;
            }

            await db.SaveChangesAsync();
        }

        // ── Intraday (5m) snapshots for the 24h chart ────────────────────────────
        // We already have fresh prices for every active coin in hand this cycle,
        // so record one "5m" history row each (no extra CoinGecko calls), then
        // prune anything older than 48h so the table stays bounded. The 24h chart
        // endpoint reads these rows straight from the DB.
        var snapNow = DateTime.UtcNow;
        foreach (var coin in activeCoins)
        {
            if (coin.PriceUsd <= 0) continue;
            db.CoinPriceHistories.Add(new CoinPriceHistory
            {
                CoinId = coin.Id,
                PriceUsd = coin.PriceUsd,
                MarketCapUsd = coin.MarketCapUsd,
                CirculatingSupply = coin.CirculatingSupply,
                Interval = "5m",
                RecordedAt = snapNow
            });
        }

        var snapCutoff = snapNow.AddHours(-48);
        var staleSnaps = await db.CoinPriceHistories
            .Where(h => h.Interval == "5m" && h.RecordedAt < snapCutoff)
            .ToListAsync();
        if (staleSnaps.Count > 0)
            db.CoinPriceHistories.RemoveRange(staleSnaps);

        await db.SaveChangesAsync();

        this.logger.LogInformation(
            "Intraday snapshot: wrote {Wrote} 5m rows, pruned {Pruned}",
            activeCoins.Count, staleSnaps.Count);

        this.logger.LogInformation(
            "Top {Top} refresh complete ({Completeness}). Coins added: {Added}, updated: {Updated}, manual: {Manual}, reactivated: {Reactivated} ({ReEntered} queued for gap repair), deactivated: {Deactivated}. History added: {HAdded}, updated: {HUpdated}. Interval: {Interval}min",
            this.topCount, isComplete ? "complete" : "INCOMPLETE", added, updated, manual, reactivated, reEntered, deactivated, historyAdded, historyUpdated, this.interval.TotalMinutes);

        // Which active coins still need their one-year daily backfill?
        //
        // This used to test "fewer than BackfillThresholdDays rows of 1d history".
        // That is unsatisfiable for any coin younger than the threshold — a coin
        // listed 60 days ago only ever has ~60 rows — so those coins were re-fetched
        // from CoinGecko on EVERY cycle (288 wasted calls/day each) and never stopped.
        //
        // Instead we keep a persistent stamp per coin: backfill when it has never
        // been done, or when it is old enough that gaps may have formed (e.g. the
        // worker was down for a while). A new entrant is backfilled exactly once.
        var refreshBefore = DateTime.UtcNow.AddDays(-BackfillRefreshDays);

        var toBackfill = await db.Coins
            .Where(c => activeIds.Contains(c.Id)
                        && (c.DailyHistoryBackfilledAtUtc == null
                            || c.DailyHistoryBackfilledAtUtc < refreshBefore))
            .Select(c => c.Id)
            .ToListAsync();

        if (toBackfill.Count > 0)
        {
            this.logger.LogInformation(
                "{Count} active coin(s) need the {Days}-day history backfill (never done, or older than {Refresh} days); queueing",
                toBackfill.Count, this.backfillService.Days, BackfillRefreshDays);
        }

        return toBackfill;
    }

    private async Task BackfillCoinsAsync(List<int> coinIds, CancellationToken ct)
    {
        // Re-checked per coin under the backfill service's lock, so a coin the startup
        // pass finished a moment ago is skipped rather than fetched (and inserted) twice.
        var refreshBefore = DateTime.UtcNow.AddDays(-BackfillRefreshDays);

        this.logger.LogInformation("Backfilling history for {Count} coin(s)", coinIds.Count);

        int rowsTotal = 0, failed = 0;
        foreach (var coinId in coinIds)
        {
            if (ct.IsCancellationRequested) break;

            var result = await this.backfillService.BackfillCoinAsync(coinId, skipIfStampedAtOrAfterUtc: refreshBefore, ct);
            if (result.Status == BackfillStatus.Failed) failed++;
            if (result.Status == BackfillStatus.NotDue) continue;
            rowsTotal += result.RowsInserted;

            if (coinIds.Count > 1)
                await Task.Delay(this.backfillDelayMs, ct);
        }

        this.logger.LogInformation("Per-cycle backfill done: {Rows} rows across {Count} coin(s), {Failed} failed",
            rowsTotal, coinIds.Count, failed);
    }

    /// <summary>
    /// Copies CoinGecko market data onto a coin and stamps <see cref="Coin.PriceUpdatedAtUtc"/>
    /// — the only "price is fresh" signal (UpdatedAt moves on any edit).
    /// </summary>
    internal static void ApplyMarketData(Coin coin, CoinGeckoMarketData m, DateTime nowUtc)
    {
        coin.PriceUsd = m.CurrentPrice ?? 0;
        coin.MarketCapUsd = m.MarketCap ?? 0;
        coin.CirculatingSupply = m.CirculatingSupply ?? 0;
        coin.PriceChangePercent24h = m.PriceChangePercentage24h ?? 0;
        coin.TotalVolume = m.TotalVolume ?? 0;
        coin.High24h = m.High24h ?? 0;
        coin.Low24h = m.Low24h ?? 0;
        coin.MarketCapRank = m.MarketCapRank ?? 0;
        coin.ImageUrl = m.Image ?? coin.ImageUrl;
        coin.PriceChangePercent1h = m.PriceChangePercentage1h ?? coin.PriceChangePercent1h;
        coin.PriceChangePercent7d = m.PriceChangePercentage7d ?? coin.PriceChangePercent7d;
        coin.PriceChangePercent30d = m.PriceChangePercentage30d ?? coin.PriceChangePercent30d;
        coin.PriceChangePercent1y = m.PriceChangePercentage1y ?? coin.PriceChangePercent1y;

        coin.Ath = m.Ath ?? coin.Ath;
        coin.AthChangePercentage = m.AthChangePercentage ?? coin.AthChangePercentage;
        coin.AthDate = m.AthDate ?? coin.AthDate;
        coin.Atl = m.Atl ?? coin.Atl;
        coin.AtlChangePercentage = m.AtlChangePercentage ?? coin.AtlChangePercentage;
        coin.AtlDate = m.AtlDate ?? coin.AtlDate;

        coin.TotalSupply = m.TotalSupply ?? coin.TotalSupply;
        coin.MaxSupply = m.MaxSupply ?? coin.MaxSupply;
        coin.FullyDilutedValuation = m.FullyDilutedValuation ?? coin.FullyDilutedValuation;

        coin.PriceUpdatedAtUtc = nowUtc;
    }

    /// <summary>
    /// One daily row per coin, tolerating duplicates: the lowest Id wins (the same row
    /// the dedupe migration keeps). Never throws on duplicate CoinIds.
    /// </summary>
    internal static Dictionary<int, CoinPriceHistory> PickDailyRowPerCoin(IEnumerable<CoinPriceHistory> rows) =>
        rows
            .GroupBy(h => h.CoinId)
            .ToDictionary(g => g.Key, g => g.OrderBy(h => h.Id).First());
}