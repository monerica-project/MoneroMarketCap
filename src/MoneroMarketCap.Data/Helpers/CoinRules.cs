using System.Linq.Expressions;
using MoneroMarketCap.Data.Models;

namespace MoneroMarketCap.Data.Helpers;

/// <summary>
/// Shared coin rules so every place that resolves "which coin does this ticker mean" or
/// "does this coin still have a page" agrees:
///   * Canonical ordering for ticker collisions: active (tracked) first, then the best
///     market-cap rank (rank 0 = unranked, sorted LAST), then larger market cap, then Id.
///     Used by the coin page resolver, /api/price, the suggest box and the Worker's
///     exchange ticker map.
///   * Resolvability: a coin page resolves while the coin is active or still inside the
///     CoinGecko:TrackingGraceDays window after leaving the top N. Anything older 404s,
///     so nothing should link to it.
/// </summary>
public static class CoinRules
{
    /// <summary>Default for CoinGecko:TrackingGraceDays when not configured.</summary>
    public const int DefaultGraceDays = 90;

    /// <summary>Oldest LastInTopNUtc that still counts as "in grace".</summary>
    public static DateTime GraceCutoff(int graceDays, DateTime nowUtc) =>
        nowUtc.AddDays(-Math.Max(0, graceDays));

    /// <summary>Query-translatable "this coin's page resolves" predicate.</summary>
    public static Expression<Func<Coin, bool>> IsResolvable(DateTime graceCutoffUtc) =>
        c => c.IsActive || (c.LastInTopNUtc != null && c.LastInTopNUtc >= graceCutoffUtc);

    /// <summary>In-memory form of <see cref="IsResolvable(DateTime)"/>.</summary>
    public static bool IsResolvable(bool isActive, DateTime? lastInTopNUtc, DateTime graceCutoffUtc) =>
        isActive || (lastInTopNUtc.HasValue && lastInTopNUtc.Value >= graceCutoffUtc);

    /// <summary>Canonical ordering (see class remarks) for a database query.</summary>
    public static IOrderedQueryable<Coin> OrderCanonical(this IQueryable<Coin> query) =>
        query
            .OrderByDescending(c => c.IsActive)
            .ThenBy(c => c.MarketCapRank == 0)
            .ThenBy(c => c.MarketCapRank)
            .ThenByDescending(c => c.MarketCapUsd)
            .ThenBy(c => c.Id);

    /// <summary>Canonical ordering (see class remarks) for in-memory sequences.</summary>
    public static IOrderedEnumerable<T> OrderCanonical<T>(
        this IEnumerable<T> source,
        Func<T, bool> isActive,
        Func<T, int> marketCapRank,
        Func<T, decimal> marketCapUsd,
        Func<T, int> id) =>
        source
            .OrderByDescending(isActive)
            .ThenBy(x => marketCapRank(x) == 0)
            .ThenBy(marketCapRank)
            .ThenByDescending(marketCapUsd)
            .ThenBy(id);
}
