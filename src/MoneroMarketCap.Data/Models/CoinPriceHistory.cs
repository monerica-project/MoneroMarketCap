namespace MoneroMarketCap.Data.Models;

public class CoinPriceHistory : AuditableEntity
{
    public int Id { get; set; }
    public int CoinId { get; set; }
    public Coin Coin { get; set; } = null!;
    public decimal PriceUsd { get; set; }
    public decimal CirculatingSupply { get; set; }
    public decimal MarketCapUsd { get; set; }
    public string Interval { get; set; } = string.Empty; // "1h", "1d", "1w"

    /// <summary>
    /// The "as-of" timestamp the price represents (e.g. the daily candle's date).
    /// This is distinct from CreatedAt, which is when the row was actually written.
    /// For real-time snapshots they'll be near-identical; for backfills they differ.
    /// </summary>
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
}