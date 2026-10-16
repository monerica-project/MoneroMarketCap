using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Helpers;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Services.Interfaces;

namespace MoneroMarketCap.Services.Implementations;

/// <summary>
/// Fills in daily history for active coins. Runs once on startup as a
/// BackgroundService, and exposes BackfillCoinAsync for on-demand calls
/// (used when a new coin enters the top N or a dropout returns).
/// Safe to re-run: existing (CoinId, RecordedAt) rows are skipped.
///
/// Concurrency: the startup pass and CoinPriceUpdateService's per-cycle backfill both
/// pick up coins with DailyHistoryBackfilledAtUtc == null. They used to process the
/// same coins at the same time, each reading "no row for day X" and both inserting it —
/// the source of the duplicate daily rows. Every per-coin backfill now runs under one
/// process-wide lock and re-reads the coin's stamp inside it, so whichever caller gets
/// there second sees the fresh stamp and skips the coin (BackfillStatus.NotDue).
/// </summary>
public class CoinHistoryBackfillService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<CoinHistoryBackfillService> logger;
    private readonly int days;
    private readonly int delayMs;
    private readonly bool enabled;

    // Serializes all per-coin backfills in this process (startup pass + per-cycle calls).
    private readonly SemaphoreSlim gate = new(1, 1);

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

        // Only coins that have never been backfilled. Previously this pass fetched a
        // year of history for EVERY active coin on every process start and threw away
        // the response for coins that were already complete — one wasted CoinGecko
        // call per coin, per deploy.
        var activeCoins = await db.Coins
            .Where(c => c.IsActive
                        && c.CoinGeckoId != null && c.CoinGeckoId != ""
                        && c.DailyHistoryBackfilledAtUtc == null)
            .Select(c => c.Id)
            .ToListAsync(ct);

        this.logger.LogInformation("Startup backfill for {Count} coin(s) with no history yet, {Days} days each",
            activeCoins.Count, this.days);

        int processed = 0, rowsInserted = 0, skipped = 0, failed = 0;

        foreach (var coinId in activeCoins)
        {
            if (ct.IsCancellationRequested) break;

            // DateTime.MinValue: skip the coin if it has been stamped at all since we
            // listed it (i.e. the per-cycle backfill already did it).
            var result = await BackfillCoinAsync(coinId, skipIfStampedAtOrAfterUtc: DateTime.MinValue, ct);
            if (result.Status == BackfillStatus.NotDue)
            {
                skipped++;
                continue;
            }

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
    /// Runs under the service-wide lock; the coin's stamp is re-read inside the lock and
    /// the coin is skipped (<see cref="BackfillStatus.NotDue"/>) when it was stamped at or
    /// after <paramref name="skipIfStampedAtOrAfterUtc"/> — i.e. another caller already
    /// did it. Pass null to always backfill.
    /// </summary>
    public async Task<BackfillResult> BackfillCoinAsync(
        int coinId, DateTime? skipIfStampedAtOrAfterUtc = null, CancellationToken ct = default)
    {
        await this.gate.WaitAsync(ct);
        try
        {
            using var scope = this.scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var gecko = scope.ServiceProvider.GetRequiredService<ICoinGeckoService>();

            // Fresh context → fresh read of the stamp, not a value cached by the caller.
            var coin = await db.Coins.FindAsync(new object[] { coinId }, ct);
            if (coin == null || string.IsNullOrEmpty(coin.CoinGeckoId))
                return new BackfillResult(BackfillStatus.Failed, 0);

            if (skipIfStampedAtOrAfterUtc.HasValue
                && coin.DailyHistoryBackfilledAtUtc.HasValue
                && coin.DailyHistoryBackfilledAtUtc.Value >= skipIfStampedAtOrAfterUtc.Value)
            {
                return new BackfillResult(BackfillStatus.NotDue, 0);
            }

            return await BackfillCoinInternalAsync(db, gecko, coin, this.days, ct);
        }
        finally
        {
            this.gate.Release();
        }
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
                return await StampAsync(db, coin, BackfillStatus.AlreadyCurrent, 0, ct);

            int insertedThisCoin = await AddMissingDailyRowsAsync(db, coin.Id, pricesByDate, ct);

            if (insertedThisCoin > 0)
            {
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
                {
                    // A row appeared between our read and insert (unique index on
                    // CoinId+Interval+RecordedAt). Drop our pending rows, re-read, and
                    // insert only what is still missing.
                    this.logger.LogInformation("Backfill {Coin}: some days were written concurrently; retrying the missing ones", coin.CoinGeckoId);
                    foreach (var entry in db.ChangeTracker.Entries<CoinPriceHistory>()
                                 .Where(e => e.State == EntityState.Added)
                                 .ToList())
                    {
                        entry.State = EntityState.Detached;
                    }

                    insertedThisCoin = await AddMissingDailyRowsAsync(db, coin.Id, pricesByDate, ct);
                    await db.SaveChangesAsync(ct);
                }

                this.logger.LogInformation("Backfilled {Coin}: {Rows} rows", coin.CoinGeckoId, insertedThisCoin);
                return await StampAsync(db, coin, BackfillStatus.Inserted, insertedThisCoin, ct);
            }

            return await StampAsync(db, coin, BackfillStatus.AlreadyCurrent, 0, ct);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Backfill failed for {Id}", coin.CoinGeckoId);
            return new BackfillResult(BackfillStatus.Failed, 0);
        }
    }

    /// <summary>
    /// Queues inserts for the days in <paramref name="pricesByDate"/> that have no 1d row
    /// yet. Today (UTC) is skipped: CoinPriceUpdateService owns today's row and upserts it
    /// every cycle, so the backfill never races it for that key.
    /// </summary>
    private static async Task<int> AddMissingDailyRowsAsync(
        AppDbContext db, int coinId, Dictionary<DateTime, decimal> pricesByDate, CancellationToken ct)
    {
        var existingDates = await db.CoinPriceHistories
            .Where(h => h.CoinId == coinId && h.Interval == "1d")
            .Select(h => h.RecordedAt)
            .ToListAsync(ct);
        var existingSet = existingDates.Select(d => d.Date).ToHashSet();
        var today = DateTime.UtcNow.Date;

        int added = 0;
        foreach (var kv in pricesByDate)
        {
            if (kv.Key >= today || existingSet.Contains(kv.Key)) continue;

            db.CoinPriceHistories.Add(new CoinPriceHistory
            {
                CoinId = coinId,
                PriceUsd = kv.Value,
                MarketCapUsd = 0,
                CirculatingSupply = 0,
                Interval = "1d",
                RecordedAt = kv.Key
            });
            added++;
        }

        return added;
    }

    /// <summary>
    /// Records that this coin's daily history has been fetched, so the per-cycle
    /// selector stops re-requesting it. Stamped on any non-failed outcome — a coin
    /// whose history is already complete must be marked too, otherwise it is asked
    /// for again every cycle. Failures are deliberately left unstamped so they retry.
    /// </summary>
    private static async Task<BackfillResult> StampAsync(
        AppDbContext db, Coin coin, BackfillStatus status, int rows, CancellationToken ct)
    {
        coin.DailyHistoryBackfilledAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return new BackfillResult(status, rows);
    }
}

public enum BackfillStatus
{
    Inserted,
    AlreadyCurrent,
    Failed,

    /// <summary>Skipped: another caller backfilled this coin recently (stamp re-checked under the lock).</summary>
    NotDue
}

public record BackfillResult(BackfillStatus Status, int RowsInserted);