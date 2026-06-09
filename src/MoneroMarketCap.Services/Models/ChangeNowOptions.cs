namespace MoneroMarketCap.Services.Models;

/// <summary>
/// Configuration for ChangeNOW affiliate "trade for Monero" link generation.
/// Bound from the <c>ChangeNow</c> section of appsettings.
/// </summary>
public sealed class ChangeNowOptions
{
    public const string SectionName = "ChangeNow";

    /// <summary>Master on/off switch. Links are also suppressed if <see cref="LinkTemplate"/> is blank.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Endpoint returning the supported-currency list (ticker + network + legacyTicker).</summary>
    public string CurrenciesApiUrl { get; set; } =
        "https://api.changenow.io/v2/exchange/currencies?active=true&flow=standard";

    /// <summary>
    /// Affiliate URL template. Tokens (case-insensitive) replaced at build time:
    ///   {ticker} / {from} -> resolved source legacy ticker (e.g. bnbbsc)
    ///   {to}              -> <see cref="DestinationTicker"/>
    /// Example: https://changenow.app.link/referral?link_id=40676d9d377a6b&amp;from={ticker}&amp;to=xmr
    /// </summary>
    public string LinkTemplate { get; set; } =
        "https://changenow.app.link/referral?link_id=40676d9d377a6b&from={ticker}&to=xmr";

    /// <summary>The "to" side of every swap. Coins matching this ticker never get a link (no XMR-&gt;XMR).</summary>
    public string DestinationTicker { get; set; } = "xmr";

    /// <summary>How often (hours) the supported-currency snapshot is refreshed.</summary>
    public int RefreshHours { get; set; } = 12;

    /// <summary>HTTP timeout (seconds) for the currency-list fetch.</summary>
    public int HttpTimeoutSeconds { get; set; } = 20;

    /// <summary>
    /// Network preference ladder for disambiguating multi-chain assets (the USDT/BNB problem).
    /// Earlier entries win. Matched case-insensitively against each candidate's network.
    /// </summary>
    public List<string> NetworkPreference { get; set; } = new();

    /// <summary>
    /// Manual coin-symbol -&gt; ChangeNOW "from" ticker overrides. These win outright and do not
    /// need to exist in the live feed — the escape hatch for assets whose default network you want
    /// to pin. Keys are coin symbols (e.g. "bnb"); values are exact ChangeNOW tickers (e.g. "bnbbsc").
    /// </summary>
    public Dictionary<string, string> TickerOverrides { get; set; } = new();
}