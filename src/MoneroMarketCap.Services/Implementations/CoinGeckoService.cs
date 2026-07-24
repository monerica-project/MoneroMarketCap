using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MoneroMarketCap.Services.Interfaces;
using MoneroMarketCap.Services.Models;
using System.Text.Json;

namespace MoneroMarketCap.Services.Implementations;

public class CoinGeckoService : ICoinGeckoService
{
    private const string DefaultBaseUrl = "https://api.coingecko.com/api/v3/";
    private const string DefaultApiKeyHeader = "x-cg-demo-api-key";

    private readonly HttpClient _http;
    private readonly ILogger<CoinGeckoService> _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public CoinGeckoService(HttpClient http, ILogger<CoinGeckoService> logger, IConfiguration config)
    {
        _http = http;

        var baseUrl = config["CoinGecko:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = DefaultBaseUrl;
        if (!baseUrl.EndsWith('/'))
            baseUrl += "/";

        _http.BaseAddress = new Uri(baseUrl);
        _http.Timeout = TimeSpan.FromSeconds(120);
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
        _http.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;

        var apiKey = config["CoinGecko:ApiKey"];
        var apiKeyHeader = config["CoinGecko:ApiKeyHeader"];
        if (string.IsNullOrWhiteSpace(apiKeyHeader))
            apiKeyHeader = DefaultApiKeyHeader;

        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.Add(apiKeyHeader, apiKey);

        _logger = logger;
        _logger.LogInformation(
            "CoinGecko client configured: BaseUrl={BaseUrl}, ApiKeyHeader={Header}, ApiKeyPresent={HasKey}",
            baseUrl, apiKeyHeader, !string.IsNullOrEmpty(apiKey));
    }

    public async Task<string?> GetMarketChartAsync(string coinGeckoId, int days = 365)
    {
        try
        {
            // For multi-day ranges we force daily granularity (one point/day).
            // For days<=1 we OMIT the interval param so CoinGecko returns its
            // automatic fine granularity (~5-minute points) — a real intraday
            // series for the 24h chart. Forcing interval=daily here would yield
            // a single point and no usable line.
            var url = days <= 1
                ? $"coins/{coinGeckoId}/market_chart?vs_currency=usd&days=1"
                : $"coins/{coinGeckoId}/market_chart?vs_currency=usd&days={days}&interval=daily";
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

    public async Task<string?> GetMarketChartHourlyAsync(string coinGeckoId, int days)
    {
        try
        {
            // No interval param: CoinGecko auto-selects granularity — hourly for
            // 2–90 day ranges, 5-minute for 1 day. This yields a detailed line
            // (e.g. ~169 points for 7d, ~721 for 30d) rather than one point/day.
            days = Math.Clamp(days, 1, 90);
            var url = $"coins/{coinGeckoId}/market_chart?vs_currency=usd&days={days}";
            _logger.LogInformation("Fetching hourly chart: {Url}", _http.BaseAddress + url);

            var res = await _http.GetAsync(url);
            if (!res.IsSuccessStatusCode)
            {
                var body = await res.Content.ReadAsStringAsync();
                _logger.LogError("Hourly chart fetch failed {Status}: {Body}", (int)res.StatusCode, body);
                return null;
            }

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("prices").GetRawText();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetMarketChartHourlyAsync exception for {Id}", coinGeckoId);
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

    /// <summary>
    /// The top <paramref name="count"/> coins by market cap, paged.
    /// </summary>
    /// <remarks>
    /// per_page is held CONSTANT across pages and the result trimmed at the end.
    /// CoinGecko's "page" is an offset in units of per_page, so shrinking per_page on
    /// the final page re-requests the start of the list instead of continuing it:
    /// count=300 previously asked for per_page=250&amp;page=1 (ranks 1-250) then
    /// per_page=50&amp;page=2, which returns ranks 51-100 — duplicates, and ranks
    /// 251-300 never fetched. Harmless while count was 100 (a single page), wrong
    /// for anything larger.
    ///
    /// 250 is CoinGecko's maximum per_page, so this is also the cheapest way to page:
    /// 300 coins costs 2 calls per cycle rather than 3 at per_page=100.
    /// </remarks>
    public async Task<List<CoinGeckoMarketData>> GetTopCoinsAsync(int count = 500)
    {
        const int perPage = 250;

        var results = new List<CoinGeckoMarketData>();
        int pages = (int)Math.Ceiling((double)count / perPage);

        for (int page = 1; page <= pages; page++)
        {
            try
            {
                var url = BuildMarketsUrl(perPage: perPage, page: page);
                var response = await _http.GetStringAsync(url);
                var batch = JsonSerializer.Deserialize<List<CoinGeckoMarketData>>(response, _jsonOptions);

                if (batch == null || !batch.Any()) break;
                results.AddRange(batch);

                if (results.Count >= count) break;

                if (page < pages)
                    await Task.Delay(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CoinGecko GetTopCoins failed on page {Page}", page);
                break;
            }
        }

        // Trim: the last page overshoots whenever count isn't a multiple of per_page.
        return results.Count > count
            ? results.Take(count).ToList()
            : results;
    }

    private static string BuildMarketsUrl(string? ids = null, int perPage = 100, int page = 1)
    {
        var url = $"coins/markets?vs_currency=usd&order=market_cap_desc" +
                  $"&per_page={perPage}&page={page}" +
                  $"&price_change_percentage=1h,24h,7d,30d,1y&sparkline=false&precision=8";

        if (!string.IsNullOrEmpty(ids))
            url += $"&ids={ids}";

        return url;
    }
}