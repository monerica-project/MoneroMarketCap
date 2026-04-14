using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MoneroMarketCap.Services.Interfaces;
using MoneroMarketCap.Services.Models;
using System.Text.Json;

namespace MoneroMarketCap.Services.Implementations;

public class CoinGeckoService : ICoinGeckoService
{
    private readonly HttpClient _http;
    private readonly ILogger<CoinGeckoService> _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public CoinGeckoService(HttpClient http, ILogger<CoinGeckoService> logger, IConfiguration config)
    {
        _http = http;
        _http.BaseAddress = new Uri("https://api.coingecko.com/api/v3/");
        _http.Timeout = TimeSpan.FromSeconds(120);
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
        _http.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;

        var apiKey = config["CoinGecko:ApiKey"];
        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.Add("x-cg-demo-api-key", apiKey);

        _logger = logger;
    }

    public async Task<string?> GetMarketChartAsync(string coinGeckoId, int days = 365)
    {
        try
        {
            var url = $"coins/{coinGeckoId}/market_chart?vs_currency=usd&days={days}&interval=daily";
            _logger.LogInformation("Fetching chart: {Url}", _http.BaseAddress + url);

            var res = await _http.GetAsync(url);

            if (!res.IsSuccessStatusCode)
            {
                var body = await res.Content.ReadAsStringAsync();
                _logger.LogError("Chart fetch failed {Status}: {Body}", (int)res.StatusCode, body);
                return null;
            }

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("prices").GetRawText();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetMarketChartAsync exception for {Id}", coinGeckoId);
            return null;
        }
    }

    public async Task<List<CoinGeckoSearchResult>> SearchCoinsAsync(string query)
    {
        try
        {
            var response = await _http.GetStringAsync($"search?query={Uri.EscapeDataString(query)}");
            using var doc = JsonDocument.Parse(response);

            var results = new List<CoinGeckoSearchResult>();
            foreach (var coin in doc.RootElement.GetProperty("coins").EnumerateArray().Take(10))
            {
                results.Add(new CoinGeckoSearchResult
                {
                    Id = coin.GetProperty("id").GetString() ?? "",
                    Symbol = coin.GetProperty("symbol").GetString()?.ToUpper() ?? "",
                    Name = coin.GetProperty("name").GetString() ?? "",
                    Thumb = coin.TryGetProperty("thumb", out var t) ? t.GetString() : null
                });
            }
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CoinGecko search failed for: {Query}", query);
            return new();
        }
    }

    public async Task<CoinGeckoMarketData?> GetMarketDataAsync(string coinGeckoId)
    {
        try
        {
            var url = BuildMarketsUrl(ids: coinGeckoId, perPage: 1, page: 1);
            var response = await _http.GetStringAsync(url);
            var list = JsonSerializer.Deserialize<List<CoinGeckoMarketData>>(response, _jsonOptions);
            return list?.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CoinGecko market data failed for: {Id}", coinGeckoId);
            return null;
        }
    }

    public async Task<Dictionary<string, CoinGeckoMarketData>> GetMarketDataBatchAsync(IEnumerable<string> coinGeckoIds)
    {
        try
        {
            var idList = coinGeckoIds.ToList();
            if (!idList.Any()) return new();

            var ids = string.Join(",", idList.Select(Uri.EscapeDataString));
            var url = BuildMarketsUrl(ids: ids, perPage: 250, page: 1);
            var response = await _http.GetStringAsync(url);
            var list = JsonSerializer.Deserialize<List<CoinGeckoMarketData>>(response, _jsonOptions);
            return list?.ToDictionary(c => c.Id, c => c) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CoinGecko batch fetch failed");
            return new();
        }
    }

    public async Task<List<CoinGeckoMarketData>> GetTopCoinsAsync(int count = 500)
    {
        var results = new List<CoinGeckoMarketData>();
        int perPage = 250;
        int pages = (int)Math.Ceiling((double)count / perPage);

        for (int page = 1; page <= pages; page++)
        {
            try
            {
                var take = Math.Min(perPage, count - results.Count);
                var url = BuildMarketsUrl(perPage: take, page: page);
                var response = await _http.GetStringAsync(url);
                var batch = JsonSerializer.Deserialize<List<CoinGeckoMarketData>>(response, _jsonOptions);

                if (batch == null || !batch.Any()) break;
                results.AddRange(batch);

                if (page < pages)
                    await Task.Delay(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CoinGecko GetTopCoins failed on page {Page}", page);
                break;
            }
        }

        return results;
    }

    private static string BuildMarketsUrl(string? ids = null, int perPage = 100, int page = 1)
    {
        var url = $"coins/markets?vs_currency=usd&order=market_cap_desc" +
                  $"&per_page={perPage}&page={page}" +
                  $"&price_change_percentage=1h,24h,7d,30d&sparkline=false&precision=8";

        if (!string.IsNullOrEmpty(ids))
            url += $"&ids={ids}";

        return url;
    }
}