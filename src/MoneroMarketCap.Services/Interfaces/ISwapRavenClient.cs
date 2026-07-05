namespace MoneroMarketCap.Services.Interfaces;

/// <summary>One exchange in SwapRaven's /api/exchanges catalog, with everything needed to
/// render a self-contained profile page.</summary>
public sealed class SwapRavenCatalogExchangeDto
{
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Kind { get; set; }
    public string? Description { get; set; }
    public string WebsiteUrl { get; set; } = string.Empty;
    public string? AffiliateUrl { get; set; }
    public string? TorUrl { get; set; }
    public string? I2pUrl { get; set; }
    public string? CountryCode { get; set; }
    public string? Grade { get; set; }
    public string? Kyc { get; set; }
    public string? Aml { get; set; }
    public decimal? FeeMinPercent { get; set; }
    public decimal? FeeMaxPercent { get; set; }
    public bool FeeVariesByProvider { get; set; }
    public int? ProcessingTimeMinutes { get; set; }
    public DateTime? LaunchedAtUtc { get; set; }
    public List<SwapRavenContactDto> Contacts { get; set; } = new();

    /// <summary>Coin tickers (upper-case) the exchange supports.</summary>
    public List<string> Coins { get; set; } = new();
}

public sealed class SwapRavenContactDto
{
    public string Type { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string? Label { get; set; }
}

public interface ISwapRavenClient
{
    /// <summary>
    /// The full approved-exchange catalog from SwapRaven's /api/exchanges, graded best-first,
    /// with details, contact methods and supported coin tickers for each exchange.
    /// </summary>
    Task<IReadOnlyList<SwapRavenCatalogExchangeDto>> GetCatalogAsync(CancellationToken ct);
}
