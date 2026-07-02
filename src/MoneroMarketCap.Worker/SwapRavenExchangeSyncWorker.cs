using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Services.Interfaces;

namespace MoneroMarketCap.Worker;

/// <summary>
/// Weekly reconciliation of the CoinExchanges table against SwapRaven. For every coin it
/// fetches /api/{ticker}/exchanges and adds/updates/removes rows to match. Runs once
/// shortly after startup (to preload) and then every SwapRaven:SyncIntervalDays (default 7).
/// If a coin's fetch fails (network/5xx), its existing rows are left untouched; an empty
/// result (coin unknown to SwapRaven, HTTP 404) is treated as "no exchanges" and removes stale rows.
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

        var coins = await db.Coins.AsNoTracking()
            .Select(c => new { c.Id, c.Symbol })
            .ToListAsync(ct);

        int added = 0, updated = 0, removed = 0, coinsWithExchanges = 0, failed = 0;

        foreach (var coin in coins)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(coin.Symbol))
            {
                continue;
            }

            IReadOnlyList<SwapRavenExchangeDto> fetched;
            try
            {
                fetched = await swapraven.GetExchangesAsync(coin.Symbol, ct);
            }
            catch
            {
                // Fetch failed — leave this coin's existing rows in place and move on.
                failed++;
                continue;
            }

            var existing = await db.CoinExchanges
                .Where(e => e.CoinId == coin.Id)
                .ToListAsync(ct);
            var byUrl = existing
                .GroupBy(e => e.Url, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var now = DateTime.UtcNow;
            var order = 0;

            foreach (var ex in fetched)
            {
                if (string.IsNullOrWhiteSpace(ex.Url))
                {
                    continue;
                }

                seen.Add(ex.Url);

                if (byUrl.TryGetValue(ex.Url, out var row))
                {
                    row.Name = ex.Name;
                    row.Grade = ex.Grade;
                    row.Kyc = ex.Kyc;
                    row.Aml = ex.Aml;
                    row.FeeMinPercent = ex.FeeMinPercent;
                    row.FeeMaxPercent = ex.FeeMaxPercent;
                    row.FeeVariesByProvider = ex.FeeVariesByProvider;
                    row.SortOrder = order;
                    row.UpdatedAt = now;
                    updated++;
                }
                else
                {
                    db.CoinExchanges.Add(new CoinExchange
                    {
                        CoinId = coin.Id,
                        Name = ex.Name,
                        Url = ex.Url,
                        Grade = ex.Grade,
                        Kyc = ex.Kyc,
                        Aml = ex.Aml,
                        FeeMinPercent = ex.FeeMinPercent,
                        FeeMaxPercent = ex.FeeMaxPercent,
                        FeeVariesByProvider = ex.FeeVariesByProvider,
                        SortOrder = order,
                        UpdatedAt = now,
                    });
                    added++;
                }

                order++;
            }

            foreach (var row in existing)
            {
                if (!seen.Contains(row.Url))
                {
                    db.CoinExchanges.Remove(row);
                    removed++;
                }
            }

            if (fetched.Count > 0)
            {
                coinsWithExchanges++;
            }

            await db.SaveChangesAsync(ct);

            // Be polite to SwapRaven — small gap between coins.
            try
            {
                await Task.Delay(150, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation(
            "SwapRaven exchange sync done: {Coins} coins ({WithEx} with exchanges, {Failed} fetch-failed); +{Added} ~{Updated} -{Removed}",
            coins.Count, coinsWithExchanges, failed, added, updated, removed);
    }
}
