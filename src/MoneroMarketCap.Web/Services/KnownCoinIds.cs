using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MoneroMarketCap.Data;

namespace MoneroMarketCap.Web.Services;

/// <summary>
/// Gate for the public chart endpoints (/api/coin/{id}/chart, chart24h, chartfine): they
/// take an arbitrary CoinGecko id from the URL and memory-cache the response per id, so
/// without a check anyone could fill the cache with junk ids. Only ids of coins we
/// actually have a row for are accepted. The id set is read from the DB once and cached
/// briefly, so the check costs no query on the hot path.
/// </summary>
public static class KnownCoinIds
{
    public const int MaxIdLength = 128;

    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private const string CacheKey = "known-coingecko-ids";

    public static async Task<bool> IsKnownAsync(
        string? coinGeckoId,
        IServiceScopeFactory scopeFactory,
        IMemoryCache cache,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(coinGeckoId) || coinGeckoId.Length > MaxIdLength)
        {
            return false;
        }

        var ids = await cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var list = await db.Coins.AsNoTracking()
                .Where(c => c.CoinGeckoId != null && c.CoinGeckoId != "")
                .Select(c => c.CoinGeckoId)
                .ToListAsync(ct);
            return new HashSet<string>(list, StringComparer.Ordinal);
        });

        return ids != null && ids.Contains(coinGeckoId);
    }
}
