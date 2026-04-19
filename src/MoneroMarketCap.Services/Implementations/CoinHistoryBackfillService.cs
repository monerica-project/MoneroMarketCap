using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Services.Interfaces;

namespace MoneroMarketCap.Services.Implementations;

/// <summary>
/// Fills in daily history for active coins. Runs once on startup as a
/// BackgroundService, and exposes BackfillCoinAsync for on-demand calls
/// (used when a new coin enters the top N or a dropout returns).
/// Safe to re-run: existing (CoinId, RecordedAt) rows are skipped.
/// </summary>
public class CoinHistoryBackfillService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<CoinHistoryBackfillService> logger;
    private readonly int days;
    private readonly int delayMs;
    private readonly bool enabled;

    public CoinHistoryBackfillService(
        IServiceScopeFactory scopeFactory,
        ILogger<CoinHistoryBackfillService> logger,
        IConfiguration config)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
        this.days = config.GetValue<int>("CoinGecko:BackfillDays", 365);
        this.delayMs = config.GetValue<int>("CoinGecko:BackfillDelayMs", 2500);
        this.enabled = config.GetValue<bool>("CoinGecko:BackfillOnStartup", true);
    }

    public int Days => this.days;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!this.enabled)
        {
            this.logger.LogInformation("Startup history backfill disabled via config; skipping");
            return;
        }

        // Let the update service do its first cycle so the Coins table is populated.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        if (stoppingToken.IsCancellationRequested) return;

        try
        {
            await RunStartupBackfillAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Startup history backfill failed");
        }
    }

    private async Task RunStartupBackfillAsync(CancellationToken ct)
    {
        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var gecko = scope.ServiceProvider.GetRequiredService<ICoinGeckoService>();

        var activeCoins = await db.Coins
            .Where(c => c.IsActive && c.CoinGeckoId != null && c.CoinGeckoId != "")
            .ToListAsync(ct);

        this.logger.LogInformation("Startup backfill for {Count} active coins, {Days} days each",
            activeCoins.Count, this.days);

        int processed = 0, rowsInserted = 0, skipped = 0, failed = 0;

        foreach (var coin in activeCoins)
        {
            if (ct.IsCancellationRequested) break;

            var result = await BackfillCoinInternalAsync(db, gecko, coin, this.days, ct);
            switch (result.Status)
            {
                case BackfillStatus.Inserted:
                    rowsInserted += result.RowsInserted;
                    processed++;
                    break;
                case BackfillStatus.AlreadyCurrent:
                    skipped++;
                    processed++;
                    break;
                case BackfillStatus.Failed:
                    failed++;
                    break;
            }

            if (processed > 0 && processed % 10 == 0)
            {
                this.logger.LogInformation("Startup backfill progress: {Done}/{Total} coins, {Rows} rows",
                    processed, activeCoins.Count, rowsInserted);
            }

            await Task.Delay(this.delayMs, ct);
        }

        this.logger.LogInformation(
            "Startup backfill complete. Processed: {Processed}, rows: {Rows}, skipped: {Skipped}, failed: {Failed}",
            processed, rowsInserted, skipped, failed);
    }

    /// <summary>
    /// Backfill history for a single coin. Safe to call any time; will skip
    /// dates already present. Uses its own DB scope so callers don't need to.
    /// </summary>
    public async Task<BackfillResult> BackfillCoinAsync(int coinId, CancellationToken ct = default)
    {
        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var gecko = scope.ServiceProvider.GetRequiredService<ICoinGeckoService>();

        var coin = await db.Coins.FindAsync(new object[] { coinId }, ct);
        if (coin == null || string.IsNullOrEmpty(coin.CoinGeckoId))
            return new BackfillResult(BackfillStatus.Failed, 0);

        return await BackfillCoinInternalAsync(db, gecko, coin, this.days, ct);
    }

    private async Task<BackfillResult> BackfillCoinInternalAsync(
        AppDbContext db,
        ICoinGeckoService gecko,
        Coin coin,
        int daysToFetch,
        CancellationToken ct)
    {
        try
        {
            var pricesJson = await gecko.GetMarketChartAsync(coin.CoinGeckoId!, daysToFetch);
            if (string.IsNullOrEmpty(pricesJson))
                return new BackfillResult(BackfillStatus.Failed, 0);

            var pricesByDate = new Dictionary<DateTime, decimal>();
            using (var doc = JsonDocument.Parse(pricesJson))
            {
                foreach (var pair in doc.RootElement.EnumerateArray())
                {
                    if (pair.GetArrayLength() < 2) continue;
                    var ms = pair[0].GetInt64();
                    var priceElement = pair[1];
                    if (priceElement.ValueKind != JsonValueKind.Number) continue;

                    var date = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.Date;
                    pricesByDate[date] = priceElement.GetDecimal();
                }
            }

            if (pricesByDate.Count == 0)
                return new BackfillResult(BackfillStatus.AlreadyCurrent, 0);

            var existingDates = await db.CoinPriceHistories
                .Where(h => h.CoinId == coin.Id && h.Interval == "1d")
                .Select(h => h.RecordedAt)
                .ToListAsync(ct);
            var existingSet = existingDates.Select(d => d.Date).ToHashSet();

            int insertedThisCoin = 0;
            foreach (var kv in pricesByDate)
            {
                if (existingSet.Contains(kv.Key)) continue;

                db.CoinPriceHistories.Add(new CoinPriceHistory
                {
                    CoinId = coin.Id,
                    PriceUsd = kv.Value,
                    MarketCapUsd = 0,
                    CirculatingSupply = 0,
                    Interval = "1d",
                    RecordedAt = kv.Key
                });
                insertedThisCoin++;
            }

            if (insertedThisCoin > 0)
            {
                await db.SaveChangesAsync(ct);
                this.logger.LogInformation("Backfilled {Coin}: {Rows} rows", coin.CoinGeckoId, insertedThisCoin);
                return new BackfillResult(BackfillStatus.Inserted, insertedThisCoin);
            }

            return new BackfillResult(BackfillStatus.AlreadyCurrent, 0);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Backfill failed for {Id}", coin.CoinGeckoId);
            return new BackfillResult(BackfillStatus.Failed, 0);
        }
    }
}

public enum BackfillStatus
{
    Inserted,
    AlreadyCurrent,
    Failed
}

public record BackfillResult(BackfillStatus Status, int RowsInserted);