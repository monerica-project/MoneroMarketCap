namespace MoneroMarketCap.Services.Interfaces;

/// <summary>
/// Resolves ChangeNOW affiliate "trade for Monero" links for coins, backed by a
/// periodically-refreshed in-memory snapshot of ChangeNOW's supported currencies.
/// </summary>
public interface IChangeNowLinkService
{
    /// <summary>Whether link generation is enabled (configured and a template is present).</summary>
    bool Enabled { get; }

    /// <summary>True once the supported-currency snapshot has been successfully loaded at least once.</summary>
    bool IsWarm { get; }

    /// <summary>How often the supported-currency snapshot should be refreshed.</summary>
    TimeSpan RefreshInterval { get; }

    /// <summary>
    /// Returns the ChangeNOW "from" ticker (legacy ticker, e.g. <c>bnbbsc</c>) for a coin symbol,
    /// or <c>null</c> if the asset is not tradable or the cache is not warm yet. Reads the in-memory
    /// snapshot only — never performs network I/O, so it is safe on the request path.
    /// </summary>
    string? ResolveFromTicker(string coinSymbol);

    /// <summary>Builds the affiliate URL from the configured template for a known "from" ticker.</summary>
    string? BuildTradeUrl(string fromTicker);

    /// <summary>Convenience: resolve a coin symbol to its ticker and build the URL in one call.</summary>
    string? ResolveTradeUrl(string coinSymbol);

    /// <summary>
    /// Refreshes the supported-currency snapshot from the ChangeNOW API. Best-effort: on any failure
    /// the previously-cached snapshot is retained rather than cleared.
    /// </summary>
    Task RefreshAsync(CancellationToken ct = default);
}