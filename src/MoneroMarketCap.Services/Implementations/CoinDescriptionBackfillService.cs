using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MoneroMarketCap.Data;
using MoneroMarketCap.Services.Display;
using MoneroMarketCap.Services.Interfaces;

namespace MoneroMarketCap.Services.Implementations;

/// <summary>
/// Fills and slowly refreshes each active coin's description from CoinGecko's /coins/{id}
/// endpoint (one call per coin — the only endpoint that carries descriptions). Descriptions
/// change rarely, so:
///   • a coin with no description yet (DescriptionUpdatedAtUtc == null) is fetched once;
///   • a coin fetched more than DescriptionRefreshDays ago is re-fetched;
///   • coins CoinGecko simply has no description for are still stamped, so they are not
///     re-requested every pass (they just get an empty string).
/// Runs on a long interval and spaces calls out, so the added CoinGecko usage is roughly
/// one call per coin per refresh window (~300/month for 300 coins) — negligible next to
/// the price/chart workers.
/// </summary>
public class CoinDescriptionBackfillService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<CoinDescriptionBackfillService> logger;
    private readonly TimeSpan interval;
    private readonly int delayMs;
    private readonly int refreshDays;
    private readonly int perPassCap;
    private readonly bool enabled;

    public CoinDescriptionBackfillService(
        IServiceScopeFactory scopeFactory,
        ILogger<CoinDescriptionBackfillService> logger,
        IConfiguration config)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
        this.enabled = config.GetValue<bool>("CoinGecko:DescriptionsEnabled", true);
        this.interval = TimeSpan.FromHours(
            Math.Max(1, config.GetValue<int>("CoinGecko:DescriptionRefreshIntervalHours", 12)));
        this.delayMs = config.GetValue<int>("CoinGecko:DescriptionDelayMs", 2500);
        this.refreshDays = config.GetValue<int>("CoinGecko:DescriptionRefreshDays", 30);
        // Cap per pass so a huge first run doesn't monopolize the quota in one go.
        this.perPassCap = config.GetValue<int>("CoinGecko:DescriptionPerPassCap", 400);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!this.enabled)
        {
            this.logger.LogInformation("Coin descriptions disabled via config; skipping");
            return;
        }

        // Let the price update service populate the Coins table first.
        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunPassAsync(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Description backfill pass failed");
            }

            try { await Task.Delay(this.interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunPassAsync(CancellationToken ct)
    {
        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var gecko = scope.ServiceProvider.GetRequiredService<ICoinGeckoService>();

        var staleBefore = DateTime.UtcNow.AddDays(-this.refreshDays);

        // Never fetched first, then oldest refresh first.
        var due = await db.Coins
            .Where(c => c.IsActive
                        && c.CoinGeckoId != null && c.CoinGeckoId != ""
                        && (c.DescriptionUpdatedAtUtc == null
                            || c.DescriptionUpdatedAtUtc < staleBefore))
            .OrderBy(c => c.DescriptionUpdatedAtUtc == null ? 0 : 1)
            .ThenBy(c => c.DescriptionUpdatedAtUtc)
            .Take(this.perPassCap)
            .Select(c => new { c.Id, c.CoinGeckoId })
            .ToListAsync(ct);

        if (due.Count == 0)
        {
            this.logger.LogInformation("Descriptions: nothing due this pass");
            return;
        }

        this.logger.LogInformation("Descriptions: {Count} coin(s) due", due.Count);

        int updated = 0, empty = 0, failed = 0;
        foreach (var c in due)
        {
            if (ct.IsCancellationRequested) break;

            var raw = await gecko.GetCoinDescriptionAsync(c.CoinGeckoId!, ct);

            if (raw == null)
            {
                // Request failed — leave the stamp alone so it retries next pass.
                failed++;
            }
            else
            {
                var clean = CoinDescriptionSanitizer.Clean(raw);
                var coin = await db.Coins.FindAsync(new object[] { c.Id }, ct);
                if (coin != null)
                {
                    coin.Description = clean;
                    coin.DescriptionUpdatedAtUtc = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    if (clean.Length > 0) { updated++; } else { empty++; }
                }
            }

            try { await Task.Delay(this.delayMs, ct); }
            catch (OperationCanceledException) { break; }
        }

        this.logger.LogInformation(
            "Descriptions pass done: {Updated} with text, {Empty} none-on-CoinGecko, {Failed} failed",
            updated, empty, failed);
    }
}
