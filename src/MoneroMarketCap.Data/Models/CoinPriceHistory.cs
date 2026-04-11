namespace MoneroMarketCap.Data.Models;

public class CoinPriceHistory
{
    public int Id { get; set; }
    public int CoinId { get; set; }
    public Coin Coin { get; set; } = null!;
    public decimal PriceUsd { get; set; }
    public decimal CirculatingSupply { get; set; }
    public decimal MarketCapUsd { get; set; }
    public string Interval { get; set; } = string.Empty; // e.g. "1h", "1d", "1w"
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
}