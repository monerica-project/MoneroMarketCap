namespace MoneroMarketCap.Data.Models;

/// <summary>
/// One day of the two sentiment readings shown on the home page, each 0 (extreme fear) to
/// 100 (extreme greed):
///   • <see cref="MarketValue"/>: the Crypto Fear and Greed Index from alternative.me, which
///     measures the whole crypto market (mostly Bitcoin).
///   • <see cref="MoneroValue"/>: this site's own reading for Monero, worked out from XMR's
///     price history (see MoneroFearGreed).
/// One row per UTC date. Either value can be missing for a day.
/// </summary>
public class FearGreedDay : AuditableEntity
{
    public int Id { get; set; }

    /// <summary>UTC date (always 00:00:00 UTC).</summary>
    public DateTime Date { get; set; }

    public int? MarketValue { get; set; }

    public int? MoneroValue { get; set; }
}
