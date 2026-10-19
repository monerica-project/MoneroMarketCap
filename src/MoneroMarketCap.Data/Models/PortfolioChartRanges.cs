namespace MoneroMarketCap.Data.Models;

/// <summary>
/// Allowed values for <see cref="AppUser.PortfolioChartRange"/>.
/// </summary>
public static class PortfolioChartRanges
{
    public const string AllTime = "all";
    public const string LastYear = "1y";
    public const string LastMonth = "1m";

    /// <summary>Returns the value if it is a known range, otherwise all-time.</summary>
    public static string Normalize(string? value) => value switch
    {
        LastYear => LastYear,
        LastMonth => LastMonth,
        _ => AllTime
    };
}
