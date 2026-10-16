using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data.Helpers;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Data.Repositories;
using Xunit;

namespace MoneroMarketCap.Tests;

public class PortfolioTests
{
    private static CoinTransaction Tx(int id, TransactionType type, decimal amount, decimal price, int day) => new()
    {
        Id = id,
        Type = type,
        Amount = amount,
        PriceUsdAtTime = price,
        TransactedAt = new DateTime(2026, 1, day, 0, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void Calculator_UsesAverageCostForSells()
    {
        var (amount, cost) = PortfolioTotalsCalculator.Compute(new[]
        {
            Tx(1, TransactionType.Buy, 2m, 100m, 1),   // 2 @ 100 = 200
            Tx(2, TransactionType.Buy, 2m, 200m, 2),   // 4 units, cost 600
            Tx(3, TransactionType.Sell, 1m, 999m, 3),  // sell 1/4 → cost 450
        });

        Assert.Equal(3m, amount);
        Assert.Equal(450m, cost);
    }

    [Fact]
    public void Calculator_OrdersByDateAndNeverGoesNegative()
    {
        var (amount, cost) = PortfolioTotalsCalculator.Compute(new[]
        {
            Tx(2, TransactionType.Sell, 5m, 10m, 2),
            Tx(1, TransactionType.Buy, 1m, 10m, 1),
        });

        Assert.Equal(0m, amount);
        Assert.Equal(0m, cost);
    }

    private static async Task<(MoneroMarketCap.Data.AppDbContext Db, PortfolioCoin Pc)> SeedAsync(params CoinTransaction[] txs)
    {
        var db = TestDb.Create();
        var coin = new Coin { Symbol = "XMR", Name = "Monero", CoinGeckoId = "monero" };
        var pc = new PortfolioCoin { Portfolio = new Portfolio { UserId = 1 }, Coin = coin };
        foreach (var t in txs)
        {
            pc.Transactions.Add(t);
        }

        db.PortfolioCoins.Add(pc);
        await db.SaveChangesAsync();
        return (db, pc);
    }

    [Fact]
    public async Task DeleteTransaction_KeepsCoinAndRemainingHistory_WhenNetAmountIsZero()
    {
        // Buy 1, sell 1, buy 1 → deleting the last buy leaves a net 0 balance but two
        // real transactions. The old code removed the PortfolioCoin (cascade-deleting them).
        var (db, pc) = await SeedAsync(
            Tx(1, TransactionType.Buy, 1m, 100m, 1),
            Tx(2, TransactionType.Sell, 1m, 150m, 2),
            Tx(3, TransactionType.Buy, 1m, 200m, 3));

        await new PortfolioRepository(db).DeleteTransactionAsync(3);

        var reloaded = await db.PortfolioCoins.Include(p => p.Transactions).SingleAsync(p => p.Id == pc.Id);
        Assert.Equal(2, reloaded.Transactions.Count);
        Assert.Equal(0m, reloaded.TotalAmount);
        Assert.Equal(0m, reloaded.TotalCostBasis);
    }

    [Fact]
    public async Task DeleteTransaction_RecomputesWithAverageCost()
    {
        var (db, pc) = await SeedAsync(
            Tx(1, TransactionType.Buy, 2m, 100m, 1),
            Tx(2, TransactionType.Buy, 2m, 200m, 2),
            Tx(3, TransactionType.Sell, 1m, 300m, 3),
            Tx(4, TransactionType.Buy, 5m, 1m, 4));

        await new PortfolioRepository(db).DeleteTransactionAsync(4);

        var reloaded = await db.PortfolioCoins.SingleAsync(p => p.Id == pc.Id);
        Assert.Equal(3m, reloaded.TotalAmount);
        Assert.Equal(450m, reloaded.TotalCostBasis); // matches the pages' RecalculateTotalsAsync
    }

    [Fact]
    public async Task DeleteTransaction_RemovesCoin_OnlyWhenNoTransactionsLeft()
    {
        var (db, pc) = await SeedAsync(Tx(1, TransactionType.Buy, 1m, 100m, 1));

        await new PortfolioRepository(db).DeleteTransactionAsync(1);

        Assert.False(await db.PortfolioCoins.AnyAsync(p => p.Id == pc.Id));
    }
}
