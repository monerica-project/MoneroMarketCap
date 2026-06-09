using MoneroMarketCap.Services.Interfaces;

namespace MoneroMarketCap.Web.HostedServices;

/// <summary>
/// Warms the ChangeNOW supported-currency snapshot at startup and refreshes it on
/// a fixed interval, so request-path code (coin detail pages) only ever reads a warm
/// in-memory cache and never blocks on the ChangeNOW API.
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

        // Initial warm-up so the first coin-page visit after deploy already has data.
        await _changeNow.RefreshAsync(stoppingToken);

        using var timer = new PeriodicTimer(_changeNow.RefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await _changeNow.RefreshAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}