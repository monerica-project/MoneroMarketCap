namespace MoneroMarketCap.Data.Models;

/// <summary>
/// A first-class exchange, synced from SwapRaven's /api/exchanges catalog. MoneroMarketCap
/// renders its own self-contained profile pages (/exchanges/{slug}) from this — description,
/// contact info, grade/KYC/AML/fees, an affiliate "go to exchange" button, and the coins it
/// supports (via <see cref="ExchangeCoins"/>).
/// </summary>
public class Exchange
{
    public int Id { get; set; }

    /// <summary>Where this row is managed from: "SwapRaven" (synced catalog) or "Cex"
    /// (Kraken/Coinbase, synced from their public coin APIs). Each sync only reconciles
    /// its own source so it never deletes the other's rows.</summary>
    public string Source { get; set; } = "SwapRaven";

    /// <summary>URL slug for /exchanges/{slug} (matches SwapRaven's slug).</summary>
    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>"Exchange" or "Aggregator".</summary>
    public string? Kind { get; set; }

    public string? Description { get; set; }

    /// <summary>The exchange's real website.</summary>
    public string WebsiteUrl { get; set; } = string.Empty;

    /// <summary>Affiliate/referral URL, used for the outbound button when present.</summary>
    public string? AffiliateUrl { get; set; }

    public string? TorUrl { get; set; }
    public string? I2pUrl { get; set; }
    public string? CountryCode { get; set; }

    public string? Grade { get; set; }
    public string? Kyc { get; set; }
    public string? Aml { get; set; }

    /// <summary>Monerica directory profile slug (/site/{slug}) when this exchange has a public
    /// Monerica listing, else null. We link there for reviews + further details. Enriched from
    /// the Monerica directory by matching website domain (name fallback); null = no link.</summary>
    public string? MonericaSlug { get; set; }

    /// <summary>Where the exchange's swap liquidity comes from, from its Monerica listing:
    /// "Own", "Mixed", "Third Party", "Varies By Provider". Null when unknown/not applicable.</summary>
    public string? Liquidity { get; set; }

    public decimal? FeeMinPercent { get; set; }
    public decimal? FeeMaxPercent { get; set; }
    public bool FeeVariesByProvider { get; set; }

    public int? ProcessingTimeMinutes { get; set; }
    public DateTime? LaunchedAtUtc { get; set; }

    /// <summary>Graded best-first ordering (Ungraded last), as served by SwapRaven.</summary>
    public int SortOrder { get; set; }

    public DateTime UpdatedAt { get; set; }

    public ICollection<ExchangeCoin> ExchangeCoins { get; set; } = new List<ExchangeCoin>();
    public ICollection<ExchangeContact> Contacts { get; set; } = new List<ExchangeContact>();
}
