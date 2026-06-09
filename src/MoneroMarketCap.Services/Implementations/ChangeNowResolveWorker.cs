using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MoneroMarketCap.Data;
using MoneroMarketCap.Services.Interfaces;

namespace MoneroMarketCap.Services.Implementations;

/// <summary>
/// Resolves ChangeNOW "from" tickers for NEW coins only — those never checked
/// (ChangeNowCheckedAt is null). Each coin is resolved exactly once and then stamped,
/// so it never re-checks. When there are no new coins it does nothing and makes no
/// network call. Existing coins are handled by the manual backfill. Fully guarded.
/// </summary>
public sealed class ChangeNowResolveWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IChangeNowLinkService _changeNow;
    private readonly ILogger<ChangeNowResolveWorker> _logger;
    private readonly TimeSpan _interval;

    public ChangeNowResolveWorker(
        IServiceScopeFactory scopeFactory,
        IChangeNowLinkService changeNow,
        ILogger<ChangeNowResolveWorker> logger,
        IConfiguration config)
    {
        _scopeFactory = scopeFactory;
        _changeNow = changeNow;
        _logger = logger;
        var minutes = config.GetValue<int>("ChangeNow:ResolveIntervalMinutes", 15);
        _interval = TimeSpan.FromMinutes(minutes > 0 ? minutes : 15);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_changeNow.Enabled)
        {
            _logger.LogInformation("ChangeNOW disabled — resolve worker idle.");
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);

            using var timer = new PeriodicTimer(_interval);
            do
            {
                await ResolveNewCoinsAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ChangeNOW resolve worker stopped unexpectedly (host left running).");
        }
    }

    private async Task ResolveNewCoinsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var newCoins = await db.Coins
                .Where(c => c.ChangeNowCheckedAt == null)
                .ToListAsync(ct);

            if (newCoins.Count == 0)
                return; // nothing new — no network call

            await _changeNow.RefreshAsync(ct);
            if (!_changeNow.IsWarm)
            {
                _logger.LogWarning(
                    "ChangeNOW snapshot not warm after refresh; deferring {Count} new coin(s).",
                    newCoins.Count);
                return; // leave unstamped → retried next interval
            }

            var now = DateTime.UtcNow;
            var resolved = 0;
            foreach (var coin in newCoins)
            {
                coin.ChangeNowTicker = _changeNow.ResolveFromTicker(coin.Symbol);
                coin.ChangeNowCheckedAt = now;
                if (coin.ChangeNowTicker != null) resolved++;
            }

            await db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "ChangeNOW resolve: {Resolved}/{Total} new coin(s) got a ticker.",
                resolved, newCoins.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ChangeNOW resolve cycle failed; will retry next interval.");
        }
    }
}