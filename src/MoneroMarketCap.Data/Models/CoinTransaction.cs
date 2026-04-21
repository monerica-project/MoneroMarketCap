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

    /// <summary>
    /// User-supplied external reference (e.g., exchange trade ID, tx hash).
    /// Free-form, not unique — just for personal tracking.
    /// </summary>
    public string? ExternalTransactionId { get; set; }
}