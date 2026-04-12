using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data.Models;

namespace MoneroMarketCap.Data.Repositories;

public class PortfolioRepository : IPortfolioRepository
{
    private readonly AppDbContext _db;
    public PortfolioRepository(AppDbContext db) => _db = db;

    public async Task<Portfolio?> GetByIdAsync(int id) =>
        await _db.Portfolios.FindAsync(id);

    public async Task<Portfolio?> GetWithDetailsAsync(int portfolioId) =>
        await _db.Portfolios
            .Include(p => p.PortfolioCoins)
                .ThenInclude(pc => pc.Coin)
            .Include(p => p.PortfolioCoins)
                .ThenInclude(pc => pc.Transactions)
            .FirstOrDefaultAsync(p => p.Id == portfolioId);

    public async Task<IReadOnlyList<Portfolio>> GetByUserIdAsync(int userId) =>
        await _db.Portfolios
            .Include(p => p.PortfolioCoins)
                .ThenInclude(pc => pc.Coin)
            .Where(p => p.UserId == userId)
            .ToListAsync();

    public async Task<decimal> GetUserTotalValueUsdAsync(int userId)
    {
        var portfolios = await GetByUserIdAsync(userId);
        return portfolios.Sum(p => p.TotalValueUsd);
    }

    public async Task DeletePortfolioAsync(int portfolioId)
    {
        var portfolio = await _db.Portfolios
            .Include(p => p.PortfolioCoins)
                .ThenInclude(pc => pc.Transactions)
            .FirstOrDefaultAsync(p => p.Id == portfolioId);

        if (portfolio == null) return;
        _db.Portfolios.Remove(portfolio);
        await _db.SaveChangesAsync();
    }

    // PortfolioRepository
    public async Task DeleteTransactionAsync(int transactionId)
    {
        var tx = await _db.CoinTransactions
            .Include(t => t.PortfolioCoin)
            .FirstOrDefaultAsync(t => t.Id == transactionId);

        if (tx == null) return;

        _db.CoinTransactions.Remove(tx);

        // Recalculate rolled-up totals on the parent PortfolioCoin
        var pc = tx.PortfolioCoin;
        var remaining = await _db.CoinTransactions
            .Where(t => t.PortfolioCoinId == pc.Id && t.Id != transactionId)
            .ToListAsync();

        pc.TotalAmount = remaining.Sum(t => t.Type == TransactionType.Buy ? t.Amount : -t.Amount);
        pc.TotalCostBasis = remaining.Where(t => t.Type == TransactionType.Buy).Sum(t => t.TotalUsd);

        if (pc.TotalAmount <= 0)
        {
            _db.PortfolioCoins.Remove(pc);
        }

        await _db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<Portfolio>> GetAllAsync() =>
        await _db.Portfolios.ToListAsync();

    public async Task AddAsync(Portfolio entity) => await _db.Portfolios.AddAsync(entity);

    public async Task AddTransactionAsync(int portfolioId, int coinId, TransactionType type, decimal amount, decimal priceUsdAtTime, string? notes = null)
    {
        var portfolioCoin = await _db.PortfolioCoins
            .FirstOrDefaultAsync(pc => pc.PortfolioId == portfolioId && pc.CoinId == coinId);

        if (portfolioCoin == null)
        {
            portfolioCoin = new PortfolioCoin
            {
                PortfolioId = portfolioId,
                CoinId = coinId,
                TotalAmount = 0,
                TotalCostBasis = 0
            };
            await _db.PortfolioCoins.AddAsync(portfolioCoin);
            await _db.SaveChangesAsync();
        }

        // Update rolled-up totals
        if (type == TransactionType.Buy)
        {
            portfolioCoin.TotalAmount += amount;
            portfolioCoin.TotalCostBasis += amount * priceUsdAtTime;
        }
        else
        {
            portfolioCoin.TotalAmount -= amount;
            portfolioCoin.TotalCostBasis -= amount * priceUsdAtTime;
        }
        portfolioCoin.UpdatedAt = DateTime.UtcNow;

        _db.CoinTransactions.Add(new CoinTransaction
        {
            PortfolioCoinId = portfolioCoin.Id,
            Type = type,
            Amount = amount,
            PriceUsdAtTime = priceUsdAtTime,
            TransactedAt = DateTime.UtcNow,
            Notes = notes
        });

        await _db.SaveChangesAsync();
    }

    public async Task SaveChangesAsync() => await _db.SaveChangesAsync();
}