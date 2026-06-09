using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MoneroMarketCap.Services.Interfaces;
using MoneroMarketCap.Services.Models;

namespace MoneroMarketCap.Services.Implementations;

/// <summary>
/// Singleton. Holds a periodically-refreshed snapshot of ChangeNOW's supported
/// currencies (coin symbol -> "from" ticker) and builds affiliate links from the
/// configured template. The request path only ever reads the warm in-memory map;
/// the ChangeNowCacheWarmer hosted service keeps it fresh.
///
/// Matching is intentionally strict: a coin links only when its symbol exactly equals
/// a ChangeNOW (non-fiat) ticker, or an explicit override is configured. No fuzzy/name
/// matching — we never emit a link we aren't sure points at the same asset.
/// </summary>
public sealed class ChangeNowLinkService : IChangeNowLinkService
{
    private const string HttpClientName = "changenow";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ChangeNowLinkService> _logger;
    private readonly ChangeNowOptions _options;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    // Lowercased coin symbol -> ChangeNOW "from" ticker (legacy ticker, e.g. "bnbbsc").
    // Swapped wholesale on each successful refresh; never mutated in place.
    private volatile IReadOnlyDictionary<string, string>? _symbolToFrom;

    public ChangeNowLinkService(
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<ChangeNowLinkService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        _options = config.GetSection(ChangeNowOptions.SectionName).Get<ChangeNowOptions>()
                   ?? new ChangeNowOptions();
    }

    public bool Enabled => _options.Enabled && !string.IsNullOrWhiteSpace(_options.LinkTemplate);

    public TimeSpan RefreshInterval =>
        TimeSpan.FromHours(_options.RefreshHours > 0 ? _options.RefreshHours : 12);

    public string? ResolveFromTicker(string coinSymbol)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(coinSymbol))
            return null;

        var symbol = coinSymbol.Trim().ToLowerInvariant();

        // Can't trade XMR for XMR.
        if (string.Equals(symbol, _options.DestinationTicker, StringComparison.OrdinalIgnoreCase))
            return null;

        var map = _symbolToFrom;
        if (map is null)
            return null; // cache not warmed yet — warmer fills it shortly after startup

        return map.TryGetValue(symbol, out var from) ? from : null;
    }

    public string? BuildTradeUrl(string fromTicker)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(fromTicker))
            return null;

        var url = _options.LinkTemplate
            .Replace("{ticker}", fromTicker, StringComparison.OrdinalIgnoreCase)
            .Replace("{from}", fromTicker, StringComparison.OrdinalIgnoreCase)
            .Replace("{to}", _options.DestinationTicker, StringComparison.OrdinalIgnoreCase);

        // Guard against a malformed template producing a non-absolute URL that
        // would render a broken button.
        return Uri.TryCreate(url, UriKind.Absolute, out _) ? url : null;
    }

    public string? ResolveTradeUrl(string coinSymbol)
    {
        var from = ResolveFromTicker(coinSymbol);
        return from is null ? null : BuildTradeUrl(from);
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (!Enabled)
            return;

        // Acquire WITHOUT the token: a cancellation here must not throw out of this
        // method. Contention is effectively nil (single warmer, 12h interval).
        await _refreshGate.WaitAsync();
        try
        {
            var client = _httpFactory.CreateClient(HttpClientName);
            client.Timeout = TimeSpan.FromSeconds(_options.HttpTimeoutSeconds > 0 ? _options.HttpTimeoutSeconds : 20);

            using var req = new HttpRequestMessage(HttpMethod.Get, _options.CurrenciesApiUrl);
            req.Headers.Add("Accept", "application/json");
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; MoneroMarketCap/1.0; +https://moneromarketcap.com)");

            using var res = await client.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "ChangeNOW currencies fetch failed ({Status}); keeping previous cache ({Count} entries).",
                    (int)res.StatusCode,
                    _symbolToFrom?.Count ?? 0);
                return;
            }

            var json = await res.Content.ReadAsStringAsync(ct);
            var currencies = JsonSerializer.Deserialize<List<ChangeNowCurrency>>(json, JsonOpts)
                             ?? new List<ChangeNowCurrency>();

            var map = BuildSymbolMap(currencies);
            _symbolToFrom = map;

            _logger.LogInformation("ChangeNOW currencies cached: {Count} tradable symbols.", map.Count);
        }
        catch (Exception ex)
        {
            // Best-effort: ANY failure — timeout, cancellation, DNS, TLS, HTTP, parse —
            // must be swallowed. This runs inside a BackgroundService, and an unhandled
            // exception there stops the whole host (default StopHost behavior). Keep the
            // previous cache and try again next cycle.
            _logger.LogWarning(
                ex,
                "ChangeNOW currencies refresh failed; keeping previous cache ({Count} entries).",
                _symbolToFrom?.Count ?? 0);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private IReadOnlyDictionary<string, string> BuildSymbolMap(List<ChangeNowCurrency> currencies)
    {
        // Group every entry by its base ticker (lowercased). One ticker (e.g. "usdt",
        // "bnb") can appear once per network, so each group may hold several candidates
        // that we then disambiguate.
        var groups = currencies
            .Where(c => !c.IsFiat && !string.IsNullOrWhiteSpace(c.Ticker))
            .GroupBy(c => c.Ticker!.Trim().ToLowerInvariant());

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var chosen = ChooseCandidate(group.ToList());
            if (chosen is null)
                continue;

            var from = !string.IsNullOrWhiteSpace(chosen.LegacyTicker)
                ? chosen.LegacyTicker!.Trim().ToLowerInvariant()
                : chosen.Ticker!.Trim().ToLowerInvariant();

            map[group.Key] = from;
        }

        // Manual overrides win outright and need not exist in the feed — the escape hatch
        // for pinning a default network (the USDT/BNB multi-chain problem) or for forcing a
        // specific coin (keyed by its CoinGecko symbol). You verify these yourself, so they're
        // correct by construction.
        foreach (var kvp in _options.TickerOverrides)
        {
            if (string.IsNullOrWhiteSpace(kvp.Key) || string.IsNullOrWhiteSpace(kvp.Value))
                continue;
            map[kvp.Key.Trim().ToLowerInvariant()] = kvp.Value.Trim().ToLowerInvariant();
        }

        return map;
    }

    private ChangeNowCurrency? ChooseCandidate(List<ChangeNowCurrency> candidates)
    {
        if (candidates.Count == 0)
            return null;
        if (candidates.Count == 1)
            return candidates[0];

        // Prefer assets that can be sent as the "from" side, when the flag is present.
        var sendable = candidates.Where(c => c.Sell).ToList();
        var pool = sendable.Count > 0 ? sendable : candidates;

        // 1) Highest-priority network from the configured ladder.
        foreach (var preferred in _options.NetworkPreference)
        {
            var net = preferred.Trim();
            var hit = pool.FirstOrDefault(c =>
                string.Equals(c.Network?.Trim(), net, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
                return hit;
        }

        // 2) The "bare" entry whose legacy ticker equals its ticker — usually the
        //    canonical / native-chain listing for that asset.
        var bare = pool.FirstOrDefault(c =>
            string.Equals(c.LegacyTicker?.Trim(), c.Ticker?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (bare is not null)
            return bare;

        // 3) Give up gracefully — first candidate.
        return pool[0];
    }

    /// <summary>Subset of the ChangeNOW /v2/exchange/currencies item we care about.</summary>
    private sealed class ChangeNowCurrency
    {
        public string? Ticker { get; set; }

        public string? LegacyTicker { get; set; }

        public string? Network { get; set; }

        public bool IsFiat { get; set; }

        public bool Sell { get; set; }
    }
}