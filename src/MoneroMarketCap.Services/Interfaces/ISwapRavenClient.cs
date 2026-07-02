namespace MoneroMarketCap.Services.Interfaces;

/// <summary>One exchange returned by SwapRaven's /api/{ticker}/exchanges endpoint.</summary>
public sealed class SwapRavenExchangeDto
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? Grade { get; set; }
    public string? Kyc { get; set; }
    public string? Aml { get; set; }
    public decimal? FeeMinPercent { get; set; }
    public decimal? FeeMaxPercent { get; set; }
    public bool FeeVariesByProvider { get; set; }
}

public interface ISwapRavenClient
{
    /// <summary>
    /// Approved exchanges on SwapRaven that support the coin with the given ticker,
    /// graded best-first. Returns an empty list if the coin is unknown to SwapRaven.
    /// </summary>
    Task<IReadOnlyList<SwapRavenExchangeDto>> GetExchangesAsync(string ticker, CancellationToken ct);
}
