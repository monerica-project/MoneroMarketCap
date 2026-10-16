using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data.Models;

namespace MoneroMarketCap.Web.Helpers;

/// <summary>
/// Query for the /api/coins/suggest autocomplete. Everything — filtering, match-quality
/// ranking and the row limit — runs in the database, so a one-letter search no longer
/// pulls every matching coin into memory. The user's term is LIKE-escaped, so "%" and "_"
/// are matched literally instead of acting as wildcards.
/// </summary>
public static class CoinSuggest
{
    /// <summary>Longest search term accepted; longer input is truncated.</summary>
    public const int MaxTermLength = 64;

    /// <summary>The escape character used in the generated ILIKE patterns.</summary>
    public const string LikeEscape = "\\";

    /// <summary>Escapes LIKE metacharacters (\, %, _) so they match literally.</summary>
    public static string EscapeLike(string term) => term
        .Replace("\\", "\\\\")
        .Replace("%", "\\%")
        .Replace("_", "\\_");

    /// <summary>
    /// Best matches first — exact symbol, symbol prefix, name prefix, then contains — and
    /// within each tier the canonical coin order (active first, ranked before unranked,
    /// larger market cap), limited to <paramref name="take"/> rows in SQL.
    /// </summary>
    public static IQueryable<Coin> Query(IQueryable<Coin> coins, string term, int take)
    {
        var escaped = EscapeLike(term.Length > MaxTermLength ? term[..MaxTermLength] : term);
        var contains = $"%{escaped}%";
        var prefix = $"{escaped}%";

        return coins
            .Where(c => EF.Functions.ILike(c.Symbol, contains, LikeEscape)
                        || EF.Functions.ILike(c.Name, contains, LikeEscape))
            .OrderBy(c => EF.Functions.ILike(c.Symbol, escaped, LikeEscape) ? 0
                : EF.Functions.ILike(c.Symbol, prefix, LikeEscape) ? 1
                : EF.Functions.ILike(c.Name, prefix, LikeEscape) ? 2
                : 3)
            .ThenByDescending(c => c.IsActive)
            .ThenBy(c => c.MarketCapRank == 0)
            .ThenBy(c => c.MarketCapRank)
            .ThenByDescending(c => c.MarketCapUsd)
            .ThenBy(c => c.Id)
            .Take(take);
    }
}
