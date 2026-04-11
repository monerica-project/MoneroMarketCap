namespace MoneroMarketCap.Data.Models;

public class PortfolioCoin : AuditableEntity
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public Portfolio Portfolio { get; set; } = null!;
    public int CoinId { get; set; }
    public Coin Coin { get; set; } = null!;

    // Rolled-up totals updated on each transaction
    public decimal TotalAmount { get; set; }
    public decimal TotalCostBasis { get; set; } // total USD spent buying

    public ICollection<CoinTransaction> Transactions { get; set; } = new List<CoinTransaction>();

    public decimal TotalValueUsd => TotalAmount * (Coin?.PriceUsd ?? 0);
    public decimal UnrealizedPnl => TotalValueUsd - TotalCostBasis;
}