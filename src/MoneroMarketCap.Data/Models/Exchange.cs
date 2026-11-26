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

    /// <summary>Only exchanges graded A, B or C are shown on the site; D, F and ungraded ones
    /// (including the CEX feed) stay in the database but are hidden everywhere.</summary>
    public static readonly string[] ListedGrades = { "A", "B", "C" };

    public static bool IsListedGrade(string? grade) => grade is not null && ListedGrades.Contains(grade);

    /// <summary>
    /// When the exchange's website started failing uptime checks (from SwapRaven's checker,
    /// refreshed every few hours). Null while the site is reachable.
    /// </summary>
    public DateTime? WebsiteDownSinceUtc { get; set; }

    /// <summary>A site that has been failing its checks for this long is not listed or linked.</summary>
    public static readonly TimeSpan DeadLinkAfter = TimeSpan.FromHours(24);

    /// <summary>
    /// Whether the exchange is shown anywhere on the site. Three conditions, all required:
    ///   • a listed grade (A to C);
    ///   • a live profile on Monerica (<see cref="MonericaSlug"/>): an exchange Monerica does
    ///     not list, or has removed or flagged, does not belong here either;
    ///   • its website is not dead (not failing uptime checks for <see cref="DeadLinkAfter"/>).
    /// </summary>
    public static System.Linq.Expressions.Expression<Func<Exchange, bool>> Listed(DateTime nowUtc)
    {
        var cutoff = nowUtc - DeadLinkAfter;
        return e => ListedGrades.Contains(e.Grade!)
            && e.MonericaSlug != null && e.MonericaSlug != ""
            && (e.WebsiteDownSinceUtc == null || e.WebsiteDownSinceUtc > cutoff);
    }

    /// <summary>The same rule for an exchange already loaded.</summary>
    public bool IsListed(DateTime nowUtc) =>
        IsListedGrade(Grade)
        && !string.IsNullOrEmpty(MonericaSlug)
        && (WebsiteDownSinceUtc == null || WebsiteDownSinceUtc > nowUtc - DeadLinkAfter);

    public DateTime UpdatedAt { get; set; }

    public ICollection<ExchangeCoin> ExchangeCoins { get; set; } = new List<ExchangeCoin>();
    public ICollection<ExchangeContact> Contacts { get; set; } = new List<ExchangeContact>();
}
