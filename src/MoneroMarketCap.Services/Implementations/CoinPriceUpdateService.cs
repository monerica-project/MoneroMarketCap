using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Services.Interfaces;

namespace MoneroMarketCap.Services.Implementations;

public class CoinPriceUpdateService : BackgroundService
{
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
    private async Task<List<int>> UpdatePricesAsync()
    {
        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var geckoService = scope.ServiceProvider.GetRequiredService<ICoinGeckoService>();

        var topCoins = await geckoService.GetTopCoinsAsync(this.topCount);
        if (topCoins == null || topCoins.Count == 0)
        {
            this.logger.LogWarning("CoinGecko returned no top coins; skipping this cycle");
            return new();
        }

        var topIds = topCoins.Select(c => c.Id).ToHashSet();

        var existing = await db.Coins.ToListAsync();
        var existingById = existing
            .Where(c => !string.IsNullOrEmpty(c.CoinGeckoId))
            .ToDictionary(c => c.CoinGeckoId, c => c);

        var today = DateTime.UtcNow.Date;
        int added = 0, updated = 0, reactivated = 0, deactivated = 0;

        foreach (var m in topCoins)
        {
            if (!existingById.TryGetValue(m.Id, out var coin))
            {
                coin = new Coin
                {
                    CoinGeckoId = m.Id,
                    Symbol = m.Symbol?.ToUpper() ?? "",
                    Name = m.Name ?? "",
                    CreatedAt = DateTime.UtcNow
                };
                db.Coins.Add(coin);
                added++;
            }
            else
            {
                if (!coin.IsActive) reactivated++;
                updated++;
            }

            coin.IsActive = true;
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

            coin.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();

        foreach (var coin in existing)
        {
            if (string.IsNullOrEmpty(coin.CoinGeckoId)) continue;
            if (!topIds.Contains(coin.CoinGeckoId) && coin.IsActive)
            {
                coin.IsActive = false;
                deactivated++;
            }
        }

        var activeCoins = db.Coins.Local
            .Where(c => c.IsActive && topIds.Contains(c.CoinGeckoId))
            .ToList();
        var activeIds = activeCoins.Select(c => c.Id).ToList();

        var todaysRows = await db.CoinPriceHistories
            .Where(h => h.Interval == "1d"
                     && h.RecordedAt == today
                     && activeIds.Contains(h.CoinId))
            .ToListAsync();

        var todaysByCoinId = todaysRows.ToDictionary(h => h.CoinId, h => h);

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

        await db.SaveChangesAsync();

        this.logger.LogInformation(
            "Top {Top} refresh complete. Coins added: {Added}, updated: {Updated}, reactivated: {Reactivated}, deactivated: {Deactivated}. History added: {HAdded}, updated: {HUpdated}. Interval: {Interval}min",
            this.topCount, added, updated, reactivated, deactivated, historyAdded, historyUpdated, this.interval.TotalMinutes);

        // Find active coins with thin history (< threshold days) so they get backfilled.
        // This covers newly-added entrants, reactivated dropouts, and any coin that
        // ended up with incomplete history for any other reason.
        var historyCounts = await db.CoinPriceHistories
            .Where(h => h.Interval == "1d" && activeIds.Contains(h.CoinId))
            .GroupBy(h => h.CoinId)
            .Select(g => new { CoinId = g.Key, Count = g.Count() })
            .ToListAsync();

        var countByCoinId = historyCounts.ToDictionary(x => x.CoinId, x => x.Count);

        var toBackfill = activeIds
            .Where(id => !countByCoinId.TryGetValue(id, out var count) || count < this.backfillThresholdDays)
            .ToList();

        if (toBackfill.Count > 0)
        {
            this.logger.LogInformation("Found {Count} active coin(s) with <{Threshold} days of history; queueing backfill",
                toBackfill.Count, this.backfillThresholdDays);
        }

        return toBackfill;
    }

    private async Task BackfillCoinsAsync(List<int> coinIds, CancellationToken ct)
    {
        this.logger.LogInformation("Backfilling history for {Count} coin(s)", coinIds.Count);

        int rowsTotal = 0, failed = 0;
        foreach (var coinId in coinIds)
        {
            if (ct.IsCancellationRequested) break;

            var result = await this.backfillService.BackfillCoinAsync(coinId, ct);
            if (result.Status == BackfillStatus.Failed) failed++;
            rowsTotal += result.RowsInserted;

            if (coinIds.Count > 1)
                await Task.Delay(this.backfillDelayMs, ct);
        }

        this.logger.LogInformation("Per-cycle backfill done: {Rows} rows across {Count} coin(s), {Failed} failed",
            rowsTotal, coinIds.Count, failed);
    }
}