using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MoneroMarketCap.Services.Interfaces;

namespace MoneroMarketCap.Services.Implementations;

/// <summary>
/// Maintains the FiatRateHistory table.
///
///   1) On startup, runs a one-shot backfill so we have FX history covering at
///      least the configured number of days (default 400 — slightly more than a
///      year, gives the chart endpoint a buffer).
///
///   2) Then loops, calling UpdateLatestAsync every
///      <c>FiatRateHistory:RefreshIntervalHours</c> hours (default 6).
///      Six hours catches the ECB publication window (16:00 CET) regardless of
///      worker restart timing without hammering frankfurter.
///
/// This worker hits api.frankfurter.app — it does NOT touch the CoinGecko quota.
/// </summary>
public class FiatRateHistoryWorker : BackgroundService
{
    private readonly IFiatRateHistoryService _service;
    private readonly ILogger<FiatRateHistoryWorker> _logger;
    private readonly int _backfillDays;
    private readonly TimeSpan _refreshInterval;

    public FiatRateHistoryWorker(
        IFiatRateHistoryService service,
        ILogger<FiatRateHistoryWorker> logger,
        IConfiguration config)
    {
        _service = service;
        _logger = logger;

        _backfillDays = config.GetValue<int>("FiatRateHistory:BackfillDays", 400);
        if (_backfillDays < 1) _backfillDays = 400;

        var hours = config.GetValue<int>("FiatRateHistory:RefreshIntervalHours", 6);
        if (hours < 1) hours = 6;
        _refreshInterval = TimeSpan.FromHours(hours);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "FiatRateHistoryWorker starting; backfill={Days}d, refresh={Hours}h",
            _backfillDays, _refreshInterval.TotalHours);

        // ── One-shot backfill ─────────────────────────────────────────────────
        try
        {
            var inserted = await _service.BackfillAsync(_backfillDays, stoppingToken);
            _logger.LogInformation("FX history backfill complete: {Rows} row(s) inserted/updated.", inserted);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FX history backfill failed (will continue with periodic updates).");
        }

        // ── Recurring "fetch today's rate" loop ───────────────────────────────
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_refreshInterval, stoppingToken);
            }
            catch (OperationCanceledException) { break; }

            try
            {
                await _service.UpdateLatestAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FX history daily refresh cycle failed.");
            }
        }
    }
}
