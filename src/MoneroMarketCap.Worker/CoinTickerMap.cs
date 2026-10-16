using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Helpers;

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
///      rank always wins, so the choice is stable across runs and workers — and it is the
///      same shared ordering (CoinRules.OrderCanonical) the coin page and /api/price use.
///   2. Whitespace-tolerant. Symbols are trimmed so a stray space can't turn an exact
///      ticker match into a miss.
/// </summary>
internal static class CoinTickerMap
{
    public static async Task<Dictionary<string, int>> BuildAsync(AppDbContext db, CancellationToken ct)
    {
        var rows = await db.Coins.AsNoTracking()
            .Select(c => new CoinTickerRow(c.Id, c.Symbol, c.MarketCapRank, c.MarketCapUsd, c.IsActive))
            .ToListAsync(ct);

        return Build(rows);
    }

    /// <summary>
    /// Pure map builder. Rows are walked in the shared canonical order (CoinRules: active
    /// first, ranked before unranked, best rank, then larger market cap, then Id) and the
    /// first coin per ticker wins — the same coin the coin page and /api/price resolve the
    /// ticker to, so exchange support is attached to the coin whose page visitors see.
    /// </summary>
    public static Dictionary<string, int> Build(IEnumerable<CoinTickerRow> rows)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in rows.OrderCanonical(r => r.IsActive, r => r.MarketCapRank, r => r.MarketCapUsd, r => r.Id))
        {
            var ticker = c.Symbol?.Trim();
            if (string.IsNullOrWhiteSpace(ticker))
            {
                continue;
            }

            map.TryAdd(ticker, c.Id);
        }

        return map;
    }

    internal sealed record CoinTickerRow(int Id, string? Symbol, int MarketCapRank, decimal MarketCapUsd, bool IsActive);
}
