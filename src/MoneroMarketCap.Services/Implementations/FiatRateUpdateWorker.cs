using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MoneroMarketCap.Services.Interfaces;

namespace MoneroMarketCap.Services.Implementations;

/// <summary>
/// Periodically refreshes fiat exchange rates. Runs immediately at startup,
/// then once per <c>FiatRates:RefreshIntervalMinutes</c> (default: 15).
///
/// This is a single external call per cycle to open.er-api.com — it does not
/// touch the CoinGecko API quota.
/// </summary>
public class FiatRateUpdateWorker : BackgroundService
{
    private readonly IFiatRateService _service;
    private readonly ILogger<FiatRateUpdateWorker> _logger;
    private readonly TimeSpan _interval;

    public FiatRateUpdateWorker(
        IFiatRateService service,
        ILogger<FiatRateUpdateWorker> logger,
        IConfiguration config)
    {
        _service = service;
        _logger = logger;

        var minutes = config.GetValue<int>("FiatRates:RefreshIntervalMinutes", 15);
        if (minutes < 1) minutes = 15;
        _interval = TimeSpan.FromMinutes(minutes);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("FiatRateUpdateWorker starting; interval = {Interval}min", _interval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _service.RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FX refresh cycle failed");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }
    }
}
