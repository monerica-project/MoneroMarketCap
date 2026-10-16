using MoneroMarketCap.Data.Models;

namespace MoneroMarketCap.Data.Helpers;

/// <summary>
/// Single source of truth for a PortfolioCoin's rolled-up TotalAmount / TotalCostBasis.
/// Walks the transactions in TransactedAt order: buys add amount + cost; sells remove
/// amount and the proportional share of cost basis (average-cost method). Neither total
/// ever goes negative. Used by the portfolio pages and the repository so every path
/// that changes transactions produces the same totals.
/// </summary>
public static class PortfolioTotalsCalculator
{
    public static (decimal TotalAmount, decimal TotalCostBasis) Compute(IEnumerable<CoinTransaction> transactions)
    {
        decimal totalAmount = 0;
        decimal totalCostBasis = 0;

        foreach (var t in transactions.OrderBy(t => t.TransactedAt).ThenBy(t => t.Id))
        {
            if (t.Type == TransactionType.Buy)
            {
                totalAmount += t.Amount;
                totalCostBasis += t.TotalUsd;
            }
            else
            {
                if (totalAmount > 0)
                {
                    var proportion = Math.Min(t.Amount / totalAmount, 1m);
                    totalCostBasis -= totalCostBasis * proportion;
                }

                totalAmount -= t.Amount;
                if (totalAmount < 0) totalAmount = 0;
                if (totalCostBasis < 0) totalCostBasis = 0;
            }
        }

        return (totalAmount, totalCostBasis);
    }

    /// <summary>Recomputes and assigns the totals on <paramref name="portfolioCoin"/>.</summary>
    public static void Apply(PortfolioCoin portfolioCoin, IEnumerable<CoinTransaction> transactions)
    {
        var (amount, costBasis) = Compute(transactions);
        portfolioCoin.TotalAmount = amount;
        portfolioCoin.TotalCostBasis = costBasis;
    }
}
