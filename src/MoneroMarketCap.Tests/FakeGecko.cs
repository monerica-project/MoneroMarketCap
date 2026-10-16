using MoneroMarketCap.Services.Interfaces;
using MoneroMarketCap.Services.Models;

namespace MoneroMarketCap.Tests;

/// <summary>Scriptable ICoinGeckoService for worker tests.</summary>
internal sealed class FakeGecko : ICoinGeckoService
{
    private int chartCalls;

    public TopCoinsResult Top { get; set; } = new(new List<CoinGeckoMarketData>(), true);

    public Dictionary<string, CoinGeckoMarketData> Batch { get; } = new();

    public List<List<string>> BatchRequests { get; } = new();

    public string? ChartJson { get; set; }

    public TimeSpan ChartDelay { get; set; } = TimeSpan.Zero;

    public int ChartCalls => this.chartCalls;

    public static CoinGeckoMarketData Market(string id, string symbol, int rank, decimal price) => new()
    {
        Id = id,
        Symbol = symbol,
        Name = id,
        CurrentPrice = price,
        MarketCap = price * 1000,
        MarketCapRank = rank,
    };

    public Task<TopCoinsResult> GetTopCoinsAsync(int count = 500) => Task.FromResult(this.Top);

    public Task<Dictionary<string, CoinGeckoMarketData>> GetMarketDataBatchAsync(IEnumerable<string> coinGeckoIds)
    {
        var ids = coinGeckoIds.ToList();
        this.BatchRequests.Add(ids);
        return Task.FromResult(ids.Where(this.Batch.ContainsKey).ToDictionary(i => i, i => this.Batch[i]));
    }

    public async Task<string?> GetMarketChartAsync(string coinGeckoId, int days = 365)
    {
        Interlocked.Increment(ref this.chartCalls);
        if (this.ChartDelay > TimeSpan.Zero)
        {
            await Task.Delay(this.ChartDelay);
        }

        return this.ChartJson;
    }

    public Task<List<CoinGeckoSearchResult>> SearchCoinsAsync(string query) => Task.FromResult(new List<CoinGeckoSearchResult>());

    public Task<CoinGeckoMarketData?> GetMarketDataAsync(string coinGeckoId) => Task.FromResult<CoinGeckoMarketData?>(null);

    public Task<string?> GetMarketChartHourlyAsync(string coinGeckoId, int days) => Task.FromResult<string?>(null);

    public Task<string?> GetCoinDescriptionAsync(string coinGeckoId, CancellationToken ct = default) => Task.FromResult<string?>(null);
}
