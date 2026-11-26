using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Services.Display;

namespace MoneroMarketCap.Services.Implementations;

/// <summary>
/// Keeps the FearGreedDays table current:
///   • the market-wide Crypto Fear and Greed Index, read from alternative.me
///     (GET https://api.alternative.me/fng/?limit=N, free, no key, one value per day), and
///   • the Monero reading, recomputed from the stored daily XMR and BTC prices.
/// Runs in the worker only, so the website never calls an outside service for this.
/// </summary>
public class FearGreedService
{
    private const string ApiUrl = "https://api.alternative.me/fng/?limit=";

    private readonly HttpClient _http;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FearGreedService> _logger;

    public FearGreedService(HttpClient http, IServiceScopeFactory scopeFactory, ILogger<FearGreedService> logger)
    {
        _http = http;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MoneroMarketCap/1.0 (+https://moneromarketcap.com)");
    }

    /// <summary>Parses the alternative.me answer into (UTC date, value) pairs. Bad rows are skipped.</summary>
    public static List<(DateTime Date, int Value)> ParseMarket(string json)
    {
        var list = new List<(DateTime, int)>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var row in data.EnumerateArray())
        {
            if (row.TryGetProperty("value", out var v) && row.TryGetProperty("timestamp", out var t)
                && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                && long.TryParse(t.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix)
                && value is >= 0 and <= 100)
            {
                list.Add((DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.Date, value));
            }
        }

        return list;
    }

    public async Task<int> UpdateAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rows = await db.FearGreedDays.ToDictionaryAsync(r => r.Date, ct);
        int changed = 0;
        FearGreedDay RowFor(DateTime date)
        {
            date = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
            if (!rows.TryGetValue(date, out var row))
            {
                row = new FearGreedDay { Date = date };
                rows[date] = row;
                db.FearGreedDays.Add(row);
            }

            return row;
        }

        // Market-wide index: the whole history on the first run, the last two weeks after that.
        try
        {
            var limit = rows.Values.Count(r => r.MarketValue != null) < 30 ? 500 : 14;
            var json = await _http.GetStringAsync(ApiUrl + limit.ToString(CultureInfo.InvariantCulture), ct);
            foreach (var (date, value) in ParseMarket(json))
            {
                var row = RowFor(date);
                if (row.MarketValue != value)
                {
                    row.MarketValue = value;
                    row.UpdatedAt = DateTime.UtcNow;
                    changed++;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read the market fear and greed index; keeping stored values.");
        }

        // Monero reading from stored daily prices.
        async Task<List<(DateTime, decimal)>> DailyAsync(string coinGeckoId) =>
            (await db.CoinPriceHistories.AsNoTracking()
                .Where(h => h.Interval == "1d" && h.Coin.CoinGeckoId == coinGeckoId && h.PriceUsd > 0)
                .OrderBy(h => h.RecordedAt)
                .Select(h => new { h.RecordedAt, h.PriceUsd })
                .ToListAsync(ct))
            .GroupBy(h => h.RecordedAt.Date)
            .Select(g => (g.Key, g.Last().PriceUsd))
            .ToList();

        var readings = MoneroFearGreed.Compute(await DailyAsync("monero"), await DailyAsync("bitcoin"));
        var today = DateTime.UtcNow.Date;
        foreach (var (date, value) in readings)
        {
            // Past days never change; only fill gaps and keep the last few days fresh.
            var row = rows.TryGetValue(DateTime.SpecifyKind(date, DateTimeKind.Utc), out var existing) ? existing : null;
            if (row?.MoneroValue != null && date < today.AddDays(-2))
            {
                continue;
            }

            row ??= RowFor(date);
            if (row.MoneroValue != value)
            {
                row.MoneroValue = value;
                row.UpdatedAt = DateTime.UtcNow;
                changed++;
            }
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return changed;
    }
}

/// <summary>Runs <see cref="FearGreedService"/> shortly after start-up and then every few hours.</summary>
public class FearGreedWorker : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromHours(3);
    private readonly FearGreedService _service;
    private readonly ILogger<FearGreedWorker> _logger;

    public FearGreedWorker(FearGreedService service, ILogger<FearGreedWorker> logger)
    {
        _service = service;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                var changed = await _service.UpdateAsync(stoppingToken);
                _logger.LogInformation("Fear and greed update: {Changed} value(s) written.", changed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fear and greed update failed.");
            }

            try
            {
                await Task.Delay(Every, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
