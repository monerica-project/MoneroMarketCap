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
/// Keeps a persistent hourly ("1h") price series in the DB for active coins so the
/// 7D/30D charts can draw a detailed line. Runs in the Worker (which can reach
/// CoinGecko); the web box only reads these rows. Refreshes on a schedule so the
/// data is always fresh in the background — the web never calls CoinGecko itself.
///
/// Each pass fetches CoinGecko's hourly series for the last N days per coin and
/// full-refreshes that coin's "1h" rows (idempotent). Old/dropped coins are pruned.
/// </summary>
public class HourlyHistoryBackfillService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<HourlyHistoryBackfillService> logger;
    private readonly int days;
    private readonly int delayMs;
    private readonly TimeSpan refreshInterval;
    private readonly bool enabled;

    public HourlyHistoryBackfillService(
        IServiceScopeFactory scopeFactory,
        ILogger<HourlyHistoryBackfillService> logger,
        IConfiguration config)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
        this.days = Math.Clamp(config.GetValue<int>("CoinGecko:HourlyBackfillDays", 30), 2, 90);
        this.delayMs = config.GetValue<int>("CoinGecko:HourlyBackfillDelayMs", 2500);
        this.refreshInterval = TimeSpan.FromHours(
            Math.Max(1, config.GetValue<int>("CoinGecko:HourlyRefreshHours", 6)));
        this.enabled = config.GetValue<bool>("CoinGecko:HourlyBackfillEnabled", true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!this.enabled)
        {
            this.logger.LogInformation("Hourly history backfill disabled via config; skipping");
            return;
        }

        // Eager one-time fill: run as soon as the Coins table is populated rather
        // than always waiting a fixed delay, so a fresh deploy fills in quickly.
        if (!await WaitForCoinsAsync(stoppingToken))
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunPassAsync(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Hourly history backfill pass failed");
            }

            try { await Task.Delay(this.refreshInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Polls until the Coins table has active coins (the price update service writes
    /// them on its first cycle), so the eager first pass doesn't run against an empty
    /// table. A small initial delay lets that first cycle land; then we check every
    /// few seconds. Returns false if cancelled while waiting.
    /// </summary>
    private async Task<bool> WaitForCoinsAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
        catch (OperationCanceledException) { return false; }

        // Up to ~2 minutes of polling for the first deploy; normally satisfied at once.
        for (var attempt = 0; attempt < 24; attempt++)
        {
            if (ct.IsCancellationRequested) return false;
            try
            {
                using var scope = this.scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var have = await db.Coins.AnyAsync(
                    c => c.IsActive && c.CoinGeckoId != null && c.CoinGeckoId != "", ct);
                if (have) return true;
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Hourly backfill: waiting for Coins table");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { return false; }
        }

        // Ran out of patience — let the first pass try anyway; it logs a 0-coin pass.
        return true;
    }

    private async Task RunPassAsync(CancellationToken ct)
    {
        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var gecko = scope.ServiceProvider.GetRequiredService<ICoinGeckoService>();

        var activeCoins = await db.Coins
            .Where(c => c.IsActive && c.CoinGeckoId != null && c.CoinGeckoId != "")
            .Select(c => new { c.Id, c.CoinGeckoId })
            .ToListAsync(ct);

        this.logger.LogInformation("Hourly backfill: {Count} active coins, {Days}d each", activeCoins.Count, this.days);

        int updated = 0, failed = 0, rows = 0;
        foreach (var coin in activeCoins)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var raw = await gecko.GetMarketChartHourlyAsync(coin.CoinGeckoId!, this.days);
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var inserted = await RefreshCoinAsync(db, coin.Id, raw!, ct);
                    if (inserted > 0) { updated++; rows += inserted; } else { failed++; }
                }
                else
                {
                    failed++;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed++;
                this.logger.LogWarning(ex, "Hourly backfill failed for {Id}", coin.CoinGeckoId);
            }

            try { await Task.Delay(this.delayMs, ct); }
            catch (OperationCanceledException) { throw; }
        }

        this.logger.LogInformation("Hourly backfill done: {Updated} coins, {Rows} rows, {Failed} failed",
            updated, rows, failed);
    }

    // Full-refresh a coin's "1h" rows from a CoinGecko prices JSON ([[unixMs, priceUsd], ...]).
    private static async Task<int> RefreshCoinAsync(AppDbContext db, int coinId, string pricesJson, CancellationToken ct)
    {
        var points = new List<(DateTime At, decimal Price)>();
        using (var doc = JsonDocument.Parse(pricesJson))
        {
            foreach (var pair in doc.RootElement.EnumerateArray())
            {
                if (pair.GetArrayLength() < 2) continue;
                if (pair[1].ValueKind != JsonValueKind.Number) continue;
                var at = DateTimeOffset.FromUnixTimeMilliseconds(pair[0].GetInt64()).UtcDateTime;
                points.Add((at, pair[1].GetDecimal()));
            }
        }

        if (points.Count == 0) return 0;

        var existing = await db.CoinPriceHistories
            .Where(h => h.CoinId == coinId && h.Interval == "1h")
            .ToListAsync(ct);
        if (existing.Count > 0) db.CoinPriceHistories.RemoveRange(existing);

        foreach (var p in points)
        {
            db.CoinPriceHistories.Add(new CoinPriceHistory
            {
                CoinId = coinId,
                PriceUsd = p.Price,
                MarketCapUsd = 0,
                CirculatingSupply = 0,
                Interval = "1h",
                RecordedAt = p.At,
            });
        }

        await db.SaveChangesAsync(ct);
        return points.Count;
    }
}
