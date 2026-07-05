using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MoneroMarketCap.Services.Interfaces;

namespace MoneroMarketCap.Services.Implementations;

/// <summary>
/// Reads the (undocumented) SwapRaven exchange catalog: GET {base}/api/exchanges.
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

    public async Task<IReadOnlyList<SwapRavenCatalogExchangeDto>> GetCatalogAsync(CancellationToken ct)
    {
        var url = $"{_baseUrl}/api/exchanges";

        try
        {
            using var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var payload = await JsonSerializer.DeserializeAsync<ApiResponse>(stream, JsonOpts, ct);
            return payload?.Exchanges ?? new List<SwapRavenCatalogExchangeDto>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SwapRaven catalog fetch failed");
            // Signal "fetch failed" (vs. "empty catalog") so the sync can skip removals.
            throw;
        }
    }

    private sealed class ApiResponse
    {
        public List<SwapRavenCatalogExchangeDto> Exchanges { get; set; } = new();
    }
}
