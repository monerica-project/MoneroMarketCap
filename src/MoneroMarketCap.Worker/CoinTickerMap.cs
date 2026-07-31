using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data;

namespace MoneroMarketCap.Worker;

/// <summary>
/// Builds a ticker -> coin id map used to match exchange catalogs to MMC coins.
///
/// Two properties matter and were missing from the old inline "coinIdByTicker[Symbol] = Id"
/// (last-row-wins) build:
///   1. Deterministic on ticker collisions. Several unrelated projects can share a symbol
///      (e.g. "AKE"). Last-wins picked whichever duplicate happened to sort last, so an
///      exchange's coins could be attached to a coin whose page nobody sees, and the two
///      sync workers could even disagree. Here the tracked coin with the best market-cap
///      rank always wins, so the choice is stable across runs and workers.
///   2. Whitespace-tolerant. Symbols are trimmed so a stray space can't turn an exact
///      ticker match into a miss.
/// </summary>
internal static class CoinTickerMap
{
    public static async Task<Dictionary<string, int>> BuildAsync(AppDbContext db, CancellationToken ct)
    {
        var rows = await db.Coins.AsNoTracking()
            .Select(c => new { c.Id, c.Symbol, c.MarketCapRank, c.IsActive })
            .ToListAsync(ct);

        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var best = new Dictionary<string, (bool Active, int Rank)>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in rows)
        {
            var ticker = c.Symbol?.Trim();
            if (string.IsNullOrWhiteSpace(ticker))
            {
                continue;
            }

            // Rank 1 is the biggest coin; unranked (0) sorts last.
            var rank = c.MarketCapRank > 0 ? c.MarketCapRank : int.MaxValue;
            var candidate = (Active: c.IsActive, Rank: rank);

            if (best.TryGetValue(ticker, out var current))
            {
                // An active (tracked) coin beats an inactive one; otherwise the higher-cap
                // (lower rank number) coin wins. Deterministic either way.
                var better = candidate.Active != current.Active
                    ? candidate.Active
                    : candidate.Rank < current.Rank;
                if (!better)
                {
                    continue;
                }
            }

            best[ticker] = candidate;
            map[ticker] = c.Id;
        }

        return map;
    }
}
