using MoneroMarketCap.Services.Models;

namespace MoneroMarketCap.Services.Interfaces;

public interface ICoinGeckoService
{
    Task<List<CoinGeckoSearchResult>> SearchCoinsAsync(string query);
    Task<CoinGeckoMarketData?> GetMarketDataAsync(string coinGeckoId);
    Task<Dictionary<string, CoinGeckoMarketData>> GetMarketDataBatchAsync(IEnumerable<string> coinGeckoIds);
    Task<List<CoinGeckoMarketData>> GetTopCoinsAsync(int count = 500);
}