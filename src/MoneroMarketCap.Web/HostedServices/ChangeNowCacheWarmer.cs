using MoneroMarketCap.Services.Interfaces;

namespace MoneroMarketCap.Web.HostedServices;

/// <summary>
/// Warms the ChangeNOW supported-currency snapshot at startup and refreshes it on a
/// fixed interval. The entire body is guarded: a BackgroundService whose ExecuteAsync
/// throws will (by default) STOP THE HOST, so nothing here is allowed to escape —
/// the link feature must never be able to take the site down.
/// </summary>
public sealed class ChangeNowCacheWarmer : BackgroundService
{
    private readonly IChangeNowLinkService _changeNow;
    private readonly ILogger<ChangeNowCacheWarmer> _logger;

    public ChangeNowCacheWarmer(IChangeNowLinkService changeNow, ILogger<ChangeNowCacheWarmer> logger)
    {
        _changeNow = changeNow;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_changeNow.Enabled)
        {
            _logger.LogInformation("ChangeNOW link generation disabled — cache warmer idle.");
            return;
        }

        try
        {
            // Initial warm-up so the first coin-page visit after deploy already has data.
            // RefreshAsync is best-effort and never throws, but we still guard everything.
            await _changeNow.RefreshAsync(stoppingToken);

            using var timer = new PeriodicTimer(_changeNow.RefreshInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await _changeNow.RefreshAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // Last line of defence: never let the warmer fault the host.
            _logger.LogError(ex, "ChangeNOW cache warmer stopped unexpectedly (host left running).");
        }
    }
}