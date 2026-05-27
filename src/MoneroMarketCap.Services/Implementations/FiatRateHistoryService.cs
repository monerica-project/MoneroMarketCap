using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Services.Interfaces;
using MoneroMarketCap.Services.Models;
using System.Globalization;
using System.Text.Json;

namespace MoneroMarketCap.Services.Implementations;

/// <summary>
/// Historical FX rates from api.frankfurter.app (European Central Bank reference rates).
///
/// Two operations:
///   • BackfillAsync(days)   — one-shot, fills any gaps for the last N days.
///   • UpdateLatestAsync()   — daily, upserts today's row.
///
/// Frankfurter publishes Mon–Fri (around 16:00 CET); weekends and holidays have no
/// new data. We forward-fill those gaps at insert time so chart lookups can do a
/// simple date-equality join later.
///
/// API shape:
///   Range (backfill):
///     GET https://api.frankfurter.app/{start:yyyy-MM-dd}..{end:yyyy-MM-dd}?from=USD&amp;to=EUR,GBP,...
///     → { "base": "USD", "start_date": "...", "end_date": "...",
///         "rates": { "2025-05-27": { "EUR": 0.92, "GBP": 0.78, ... }, ... } }
///
///   Latest (daily):
///     GET https://api.frankfurter.app/latest?from=USD&amp;to=EUR,GBP,...
///     → { "base": "USD", "date": "2026-05-27",
///         "rates": { "EUR": 0.92, "GBP": 0.78, ... } }
/// </summary>
public class FiatRateHistoryService : IFiatRateHistoryService
{
    private const string BaseUrl = "https://api.frankfurter.app";

    private readonly HttpClient _http;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FiatRateHistoryService> _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public FiatRateHistoryService(
        HttpClient http,
        IServiceScopeFactory scopeFactory,
        ILogger<FiatRateHistoryService> logger)
    {
        _http = http;
        _scopeFactory = scopeFactory;
        _logger = logger;

        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "MoneroMarketCap/1.0 (+https://moneromarketcap.com)");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<int> BackfillAsync(int days, CancellationToken ct = default)
    {
        if (days < 1) days = 1;

        var targetCodes = SupportedNonUsdCodes();
        if (targetCodes.Count == 0)
        {
            _logger.LogInformation("FX history backfill skipped: no non-USD currencies configured.");
            return 0;
        }

        // Pad the start by a few days so we have an anchor for forward-filling
        // if the requested start date lands on a weekend.
        var endDate = DateTime.UtcNow.Date;
        var startDate = endDate.AddDays(-days);
        var fetchStart = startDate.AddDays(-7);

        var url = $"{BaseUrl}/{fetchStart:yyyy-MM-dd}..{endDate:yyyy-MM-dd}"
                + $"?from=USD&to={string.Join(",", targetCodes)}";

        _logger.LogInformation(
            "FX history backfill: requesting {Days} days ({Start:yyyy-MM-dd} → {End:yyyy-MM-dd}) for {Count} currencies",
            days, startDate, endDate, targetCodes.Count);

        // raw[code][date] = ratePerUsd  (only days frankfurter actually published)
        var raw = await FetchRangeAsync(url, ct);
        if (raw == null || raw.Count == 0)
        {
            _logger.LogWarning("FX history backfill: no data returned from frankfurter.");
            return 0;
        }

        // Forward-fill each currency over the FULL requested date range.
        var allRows = new List<FiatRateHistory>();
        foreach (var code in targetCodes)
        {
            if (!raw.TryGetValue(code, out var byDate) || byDate.Count == 0)
            {
                _logger.LogWarning("FX history backfill: no data for {Code} — skipping.", code);
                continue;
            }

            // Walk every day in the requested window. For days without a fresh
            // rate (weekends, holidays), carry forward the most recent value.
            decimal? carry = null;

            // Seed `carry` from any available pre-start data (the 7-day pad above).
            foreach (var (d, r) in byDate.Where(kv => kv.Key < startDate).OrderBy(kv => kv.Key))
            {
                carry = r;
            }

            for (var d = startDate; d <= endDate; d = d.AddDays(1))
            {
                if (byDate.TryGetValue(d, out var freshRate))
                {
                    carry = freshRate;
                }

                if (carry.HasValue)
                {
                    allRows.Add(new FiatRateHistory
                    {
                        Code = code,
                        Date = d,
                        RatePerUsd = carry.Value
                    });
                }
                // If still no anchor (e.g. requested range starts before frankfurter
                // has any data for this currency), skip the day silently.
            }
        }

        if (allRows.Count == 0)
        {
            _logger.LogWarning("FX history backfill: nothing to insert after processing.");
            return 0;
        }

        return await UpsertRowsAsync(allRows, ct);
    }

    public async Task<int> UpdateLatestAsync(CancellationToken ct = default)
    {
        var targetCodes = SupportedNonUsdCodes();
        if (targetCodes.Count == 0) return 0;

        var url = $"{BaseUrl}/latest?from=USD&to={string.Join(",", targetCodes)}";

        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError(
                    "Frankfurter /latest returned {Status}: {Body}",
                    (int)response.StatusCode, body);
                return 0;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            if (!root.TryGetProperty("rates", out var ratesEl)
                || ratesEl.ValueKind != JsonValueKind.Object)
            {
                _logger.LogError("Frankfurter /latest response missing 'rates' object.");
                return 0;
            }

            // Always store under today's UTC date. If today is a weekend or holiday,
            // frankfurter's 'date' field will be the most recent business day — its
            // rate becomes today's forward-filled value.
            var today = DateTime.UtcNow.Date;
            var rows = new List<FiatRateHistory>();

            foreach (var prop in ratesEl.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Number) continue;
                if (!prop.Value.TryGetDecimal(out var rate) || rate <= 0) continue;

                var code = prop.Name.ToUpperInvariant();
                if (!targetCodes.Contains(code)) continue;

                rows.Add(new FiatRateHistory
                {
                    Code = code,
                    Date = today,
                    RatePerUsd = rate
                });
            }

            if (rows.Count == 0)
            {
                _logger.LogWarning("Frankfurter /latest had no matching currencies.");
                return 0;
            }

            return await UpsertRowsAsync(rows, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FX history daily update failed.");
            return 0;
        }
    }

    public async Task<IReadOnlyDictionary<DateTime, decimal>> GetSeriesAsync(
        string code,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct = default)
    {
        var normalized = (code ?? "").Trim().ToUpperInvariant();

        // USD is trivially 1.0 every day — never touch the DB for it.
        if (string.Equals(normalized, "USD", StringComparison.Ordinal))
        {
            var dict = new Dictionary<DateTime, decimal>();
            for (var d = fromUtc.Date; d <= toUtc.Date; d = d.AddDays(1))
                dict[d] = 1m;
            return dict;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rows = await db.FiatRateHistories
            .AsNoTracking()
            .Where(f => f.Code == normalized
                     && f.Date >= fromUtc.Date
                     && f.Date <= toUtc.Date)
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Date, r => r.RatePerUsd);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Internals
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// All currencies we display, minus USD (which is always 1.0 and isn't stored).
    /// </summary>
    private static HashSet<string> SupportedNonUsdCodes() =>
        new(
            CurrencyCatalog.SupportedCodes
                .Where(c => !string.Equals(c, "USD", StringComparison.OrdinalIgnoreCase))
                .Select(c => c.ToUpperInvariant()),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Hits frankfurter's range endpoint and returns code → (date → rate). Only
    /// dates frankfurter actually published are present in the inner dictionary;
    /// callers are responsible for forward-filling missing days.
    /// </summary>
    private async Task<Dictionary<string, Dictionary<DateTime, decimal>>?> FetchRangeAsync(
        string url, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError(
                    "Frankfurter range fetch failed {Status}: {Body}",
                    (int)response.StatusCode, body);
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            if (!root.TryGetProperty("rates", out var ratesEl)
                || ratesEl.ValueKind != JsonValueKind.Object)
            {
                _logger.LogError("Frankfurter range response missing 'rates' object.");
                return null;
            }

            var result = new Dictionary<string, Dictionary<DateTime, decimal>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var dateProp in ratesEl.EnumerateObject())
            {
                if (!DateTime.TryParseExact(
                        dateProp.Name,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var date))
                {
                    continue;
                }
                date = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);

                if (dateProp.Value.ValueKind != JsonValueKind.Object) continue;

                foreach (var codeProp in dateProp.Value.EnumerateObject())
                {
                    if (codeProp.Value.ValueKind != JsonValueKind.Number) continue;
                    if (!codeProp.Value.TryGetDecimal(out var rate) || rate <= 0) continue;

                    var code = codeProp.Name.ToUpperInvariant();
                    if (!result.TryGetValue(code, out var byDate))
                    {
                        byDate = new Dictionary<DateTime, decimal>();
                        result[code] = byDate;
                    }
                    byDate[date] = rate;
                }
            }

            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Frankfurter range fetch threw.");
            return null;
        }
    }

    /// <summary>
    /// Idempotent upsert: existing (Code, Date) rows are updated in-place, new ones inserted.
    /// </summary>
    private async Task<int> UpsertRowsAsync(IReadOnlyList<FiatRateHistory> rows, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Pull existing rows for the affected (Code, Date) windows in one query
        // so we can decide insert vs update without per-row lookups.
        var codes = rows.Select(r => r.Code).Distinct().ToList();
        var minDate = rows.Min(r => r.Date);
        var maxDate = rows.Max(r => r.Date);

        var existing = await db.FiatRateHistories
            .Where(f => codes.Contains(f.Code)
                     && f.Date >= minDate
                     && f.Date <= maxDate)
            .ToListAsync(ct);

        var existingByKey = existing.ToDictionary(
            f => (f.Code, f.Date),
            f => f);

        int added = 0, updated = 0;
        foreach (var row in rows)
        {
            if (existingByKey.TryGetValue((row.Code, row.Date), out var current))
            {
                if (current.RatePerUsd != row.RatePerUsd)
                {
                    current.RatePerUsd = row.RatePerUsd;
                    updated++;
                }
            }
            else
            {
                db.FiatRateHistories.Add(row);
                added++;
            }
        }

        if (added + updated > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        _logger.LogInformation(
            "FX history upsert complete: {Added} added, {Updated} updated, {Skipped} unchanged",
            added, updated, rows.Count - added - updated);

        return added + updated;
    }
}
