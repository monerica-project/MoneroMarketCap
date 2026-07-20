using System.Linq;
using MoneroMarketCap.Data.Models;

namespace MoneroMarketCap.Web.Helpers;

/// <summary>
/// Builds a URL-safe route key for a coin. Most coins use their ticker (e.g. "btc"),
/// but a few have non-ASCII tickers (e.g. the Chinese "币安人生"). A non-ASCII path
/// breaks HTTP header values built from the request path (Onion-Location, redirects),
/// which 500s the page. For those coins we fall back to the always-ASCII CoinGecko id
/// (e.g. "bianrensheng"), so the page lives at a clean /coins/bianrensheng URL.
/// </summary>
public static class CoinRoute
{
    /// <summary>True when a ticker is safe to place directly in a URL path segment.</summary>
    public static bool IsUrlSafe(string? s) =>
        !string.IsNullOrEmpty(s) &&
        s.All(ch => ch < 128 && (char.IsLetterOrDigit(ch) || ch == '.' || ch == '_' || ch == '-'));

    /// <summary>Canonical, lowercase, URL-safe route key for a coin.</summary>
    public static string Key(string? symbol, string? coinGeckoId)
    {
        if (IsUrlSafe(symbol))
        {
            return symbol!.Trim().ToLowerInvariant();
        }

        if (!string.IsNullOrWhiteSpace(coinGeckoId))
        {
            return coinGeckoId!.Trim().ToLowerInvariant();
        }

        // Last resort: percent-encode the ticker so it at least remains a valid path.
        return System.Uri.EscapeDataString(symbol ?? string.Empty);
    }

    public static string Key(Coin c) => Key(c.Symbol, c.CoinGeckoId);
}
