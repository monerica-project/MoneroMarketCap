using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MoneroMarketCap.Services.Interfaces;

namespace MoneroMarketCap.Services.Implementations;

/// <summary>
/// Reads the (undocumented) SwapRaven exchange API: GET {base}/api/{ticker}/exchanges.
/// Base URL comes from config "SwapRaven:ApiBaseUrl" (default https://swapraven.com).
/// </summary>
public sealed class SwapRavenClient : ISwapRavenClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly ILogger<SwapRavenClient> _logger;
    private readonly string _baseUrl;

    public SwapRavenClient(HttpClient http, IConfiguration config, ILogger<SwapRavenClient> logger)
    {
        _http = http;
        _logger = logger;
        _baseUrl = (config["SwapRaven:ApiBaseUrl"] ?? "https://swapraven.com").TrimEnd('/');
    }

    public async Task<IReadOnlyList<SwapRavenExchangeDto>> GetExchangesAsync(string ticker, CancellationToken ct)
    {
        ticker = (ticker ?? string.Empty).Trim();
        if (ticker.Length == 0)
        {
            return Array.Empty<SwapRavenExchangeDto>();
        }

        var url = $"{_baseUrl}/api/{Uri.EscapeDataString(ticker)}/exchanges";

        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                // Coin unknown to SwapRaven — no exchanges, not an error.
                return Array.Empty<SwapRavenExchangeDto>();
            }

            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var payload = await JsonSerializer.DeserializeAsync<ApiResponse>(stream, JsonOpts, ct);
            return payload?.Exchanges ?? new List<SwapRavenExchangeDto>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SwapRaven exchange fetch failed for {Ticker}", ticker);
            // Signal "fetch failed" (vs. "no exchanges") so the sync can skip removals.
            throw;
        }
    }

    private sealed class ApiResponse
    {
        public List<SwapRavenExchangeDto> Exchanges { get; set; } = new();
    }
}
