using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Data.Repositories.Interfaces;

namespace MoneroMarketCap.Data.Repositories;

public interface IPortfolioRepository : IRepository<Portfolio>
{
    Task<Portfolio?> GetWithDetailsAsync(int portfolioId);
    Task<IReadOnlyList<Portfolio>> GetByUserIdAsync(int userId);
    Task<decimal> GetUserTotalValueUsdAsync(int userId);
    Task AddTransactionAsync(int portfolioId, int coinId, TransactionType type, decimal amount, decimal priceUsdAtTime, string? notes = null);
}