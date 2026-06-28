using MoneroMarketCap.Services.Models;

namespace MoneroMarketCap.Services.Interfaces;

public interface ICoinGeckoService
{
    Task<List<CoinGeckoSearchResult>> SearchCoinsAsync(string query);
    Task<CoinGeckoMarketData?> GetMarketDataAsync(string coinGeckoId);
    Task<Dictionary<string, CoinGeckoMarketData>> GetMarketDataBatchAsync(IEnumerable<string> coinGeckoIds);
    Task<List<CoinGeckoMarketData>> GetTopCoinsAsync(int count = 500);

    Task<string?> GetMarketChartAsync(string coinGeckoId, int days = 365);

    /// <summary>
    /// Market chart at CoinGecko's automatic fine granularity (hourly for 2–90 day
    /// ranges) — i.e. the interval param is omitted. Used to draw detailed 7D/30D
    /// lines instead of the sparse one-point-per-day series.
    /// </summary>
    Task<string?> GetMarketChartHourlyAsync(string coinGeckoId, int days);
}