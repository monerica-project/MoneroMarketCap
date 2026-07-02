namespace MoneroMarketCap.Data.Models;

/// <summary>
/// An exchange (sourced from SwapRaven) that supports a given coin. Populated and
/// kept in sync by the weekly SwapRavenExchangeSyncWorker from SwapRaven's
/// /api/{ticker}/exchanges endpoint. Rows are added/updated/removed to match.
/// </summary>
public class CoinExchange
{
    public int Id { get; set; }

    public int CoinId { get; set; }
    public Coin? Coin { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The exchange's profile page on SwapRaven (where the link points).</summary>
    public string Url { get; set; } = string.Empty;

    public string? Grade { get; set; }
    public string? Kyc { get; set; }
    public string? Aml { get; set; }

    public decimal? FeeMinPercent { get; set; }
    public decimal? FeeMaxPercent { get; set; }
    public bool FeeVariesByProvider { get; set; }

    /// <summary>Preserves SwapRaven's ordering (graded best-first).</summary>
    public int SortOrder { get; set; }

    public DateTime UpdatedAt { get; set; }
}
