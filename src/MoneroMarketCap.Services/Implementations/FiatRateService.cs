using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Services.Interfaces;
using MoneroMarketCap.Services.Models;
using System.Text.Json;

namespace MoneroMarketCap.Services.Implementations;

/// <summary>
/// Reads FX rates from the DB and refreshes them from open.er-api.com.
///
/// open.er-api.com is free, requires no API key, and updates daily. We sync
/// once every ~15 minutes (configurable) which is well under their fair-use
/// limits and keeps zero pressure on the CoinGecko quota.
///
/// Endpoint shape:
///   GET https://open.er-api.com/v6/latest/USD
///   {
///     "result": "success",
///     "base_code": "USD",
///     "rates": { "USD": 1, "EUR": 0.921, "JPY": 156.42, "ARS": 1180.0, ... }
///   }
/// </summary>
public class FiatRateService : IFiatRateService
{
    private const string EndpointUrl = "https://open.er-api.com/v6/latest/USD";

    private readonly HttpClient _http;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FiatRateService> _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public FiatRateService(
        HttpClient http,
        IServiceScopeFactory scopeFactory,
        ILogger<FiatRateService> logger)
    {
        _http = http;
        _scopeFactory = scopeFactory;
        _logger = logger;

        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MoneroMarketCap/1.0 (+https://moneromarketcap.com)");
    }

    public async Task<IReadOnlyDictionary<string, decimal>> GetRatesAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rows = await db.FiatRates
            .AsNoTracking()
            .ToListAsync(ct);

        var dict = rows.ToDictionary(
            r => r.Code,
            r => r.RatePerUsd,
            StringComparer.OrdinalIgnoreCase);

        // Always guarantee USD is present so the consumer never has to special-case it.
        dict[CurrencyCatalog.DefaultCode] = 1m;

        return dict;
    }

    public async Task<int> RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Fetching FX rates from {Url}", EndpointUrl);

            var response = await _http.GetAsync(EndpointUrl, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("FX fetch failed {Status}: {Body}", (int)response.StatusCode, body);
                return 0;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            if (!root.TryGetProperty("result", out var resultEl)
                || resultEl.GetString() != "success")
            {
                _logger.LogError("FX response not 'success': {Json}", root.GetRawText());
                return 0;
            }

            if (!root.TryGetProperty("rates", out var ratesEl)
                || ratesEl.ValueKind != JsonValueKind.Object)
            {
                _logger.LogError("FX response missing rates object");
                return 0;
            }

            // Only pull rates for currencies we actually display, so the table stays small.
            var wanted = new HashSet<string>(CurrencyCatalog.SupportedCodes, StringComparer.OrdinalIgnoreCase);
            var fetched = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

            foreach (var prop in ratesEl.EnumerateObject())
            {
                if (!wanted.Contains(prop.Name)) continue;
                if (prop.Value.ValueKind != JsonValueKind.Number) continue;
                if (!prop.Value.TryGetDecimal(out var rate)) continue;
                if (rate <= 0) continue;

                fetched[prop.Name.ToUpperInvariant()] = rate;
            }

            // Make sure USD is in there with rate 1 (the upstream includes it, but defensive).
            fetched[CurrencyCatalog.DefaultCode] = 1m;

            if (fetched.Count == 0)
            {
                _logger.LogWarning("FX response had no matching currencies");
                return 0;
            }

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var existing = await db.FiatRates.ToListAsync(ct);
            var existingByCode = existing.ToDictionary(r => r.Code, StringComparer.OrdinalIgnoreCase);

            int added = 0, updated = 0;
            foreach (var (code, rate) in fetched)
            {
                if (existingByCode.TryGetValue(code, out var row))
                {
                    row.RatePerUsd = rate;
                    updated++;
                }
                else
                {
                    db.FiatRates.Add(new FiatRate { Code = code, RatePerUsd = rate });
                    added++;
                }
            }

            await db.SaveChangesAsync(ct);

            _logger.LogInformation("FX refresh complete: {Added} added, {Updated} updated", added, updated);
            return added + updated;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FX refresh failed");
            return 0;
        }
    }
}
