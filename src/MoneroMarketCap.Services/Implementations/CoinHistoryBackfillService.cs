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
/// One-shot backfill on startup. For each active coin, pulls the daily
/// market_chart from CoinGecko and inserts any missing 1d history rows.
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!this.enabled)
        {
            this.logger.LogInformation("History backfill disabled via config; skipping");
            return;
        }

        // Let the main update service do its first cycle so the Coins table is populated
        // before we try to backfill history for them.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        if (stoppingToken.IsCancellationRequested) return;

        try
        {
            await RunBackfillAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "History backfill failed");
        }
    }

    private async Task RunBackfillAsync(CancellationToken ct)
    {
        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var gecko = scope.ServiceProvider.GetRequiredService<ICoinGeckoService>();

        var activeCoins = await db.Coins
            .Where(c => c.IsActive && c.CoinGeckoId != null && c.CoinGeckoId != "")
            .ToListAsync(ct);

        this.logger.LogInformation("Backfill starting for {Count} active coins, {Days} days each",
            activeCoins.Count, this.days);

        int coinsProcessed = 0, rowsInserted = 0, coinsSkipped = 0, coinsFailed = 0;

        foreach (var coin in activeCoins)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var pricesJson = await gecko.GetMarketChartAsync(coin.CoinGeckoId!, this.days);
                if (string.IsNullOrEmpty(pricesJson))
                {
                    coinsFailed++;
                    continue;
                }

                // market_chart "prices" is an array of [unix_ms, price] pairs.
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
                        var price = priceElement.GetDecimal();

                        // CoinGecko daily points are one per day; last write wins if duplicates.
                        pricesByDate[date] = price;
                    }
                }

                if (pricesByDate.Count == 0)
                {
                    coinsSkipped++;
                    continue;
                }

                // What do we already have for this coin?
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
                        // We only have price from market_chart; leave mcap/supply at
                        // 0 for historical points — the live updater fills current day.
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
                    rowsInserted += insertedThisCoin;
                }
                else
                {
                    coinsSkipped++;
                }

                coinsProcessed++;

                if (coinsProcessed % 10 == 0)
                {
                    this.logger.LogInformation("Backfill progress: {Done}/{Total} coins, {Rows} rows inserted",
                        coinsProcessed, activeCoins.Count, rowsInserted);
                }
            }
            catch (Exception ex)
            {
                coinsFailed++;
                this.logger.LogWarning(ex, "Backfill failed for {Id}", coin.CoinGeckoId);
            }

            // Rate-limit pacing for CoinGecko demo tier.
            await Task.Delay(this.delayMs, ct);
        }

        this.logger.LogInformation(
            "Backfill complete. Processed: {Processed}, rows inserted: {Rows}, skipped (already current): {Skipped}, failed: {Failed}",
            coinsProcessed, rowsInserted, coinsSkipped, coinsFailed);
    }
}
