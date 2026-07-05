namespace MoneroMarketCap.Data.Models;

/// <summary>Join row: an <see cref="Exchange"/> supports a <see cref="Coin"/>.</summary>
public class ExchangeCoin
{
    public int Id { get; set; }

    public int ExchangeId { get; set; }
    public Exchange? Exchange { get; set; }

    public int CoinId { get; set; }
    public Coin? Coin { get; set; }
}
