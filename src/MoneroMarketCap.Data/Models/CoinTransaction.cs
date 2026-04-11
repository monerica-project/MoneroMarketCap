namespace MoneroMarketCap.Data.Models;

public enum TransactionType { Buy, Sell }

public class CoinTransaction : AuditableEntity
{
    public int Id { get; set; }
    public int PortfolioCoinId { get; set; }
    public PortfolioCoin PortfolioCoin { get; set; } = null!;
    public TransactionType Type { get; set; }
    public decimal Amount { get; set; }
    public decimal PriceUsdAtTime { get; set; }
    public decimal TotalUsd => Amount * PriceUsdAtTime;
    public DateTime TransactedAt { get; set; } = DateTime.UtcNow;
    public string? Notes { get; set; }
}