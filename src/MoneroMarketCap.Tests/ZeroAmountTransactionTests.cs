using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Data.Repositories;
using MoneroMarketCap.Pages.Portfolios;
using Xunit;

namespace MoneroMarketCap.Tests;

// A transaction with a 0 (or negative) amount is meaningless, so it can't be added or saved.
public class ZeroAmountTransactionTests
{
    private sealed class NoTempDataStore : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }

    private static async Task<(MoneroMarketCap.Data.AppDbContext Db, PortfolioCoin Pc)> SeedAsync()
    {
        var db = TestDb.Create();
        var pc = new PortfolioCoin
        {
            Portfolio = new Portfolio { UserId = 1 },
            Coin = new Coin { Symbol = "XMR", Name = "Monero", CoinGeckoId = "monero" },
        };
        pc.Transactions.Add(new CoinTransaction { Id = 1, Type = TransactionType.Buy, Amount = 2m, PriceUsdAtTime = 100m, TransactedAt = DateTime.UtcNow });
        db.PortfolioCoins.Add(pc);
        await db.SaveChangesAsync();
        return (db, pc);
    }

    private static TransactionsModel Page(MoneroMarketCap.Data.AppDbContext db)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "1") }, "test")),
        };
        return new TransactionsModel(db, new PortfolioRepository(db))
        {
            PageContext = new Microsoft.AspNetCore.Mvc.RazorPages.PageContext { HttpContext = http },
            TempData = new TempDataDictionary(http, new NoTempDataStore()),
        };
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Add_rejects_zero_or_negative_amount(int amount)
    {
        var (db, pc) = await SeedAsync();
        var page = Page(db);
        page.Amount = amount;
        page.PriceUsdAtTime = 100m;

        var result = await page.OnPostAddAsync(pc.Id);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal(1, await db.CoinTransactions.CountAsync());
        Assert.Equal("Amount must be greater than 0.", page.TempData["ImportMessage"]);
    }

    [Fact]
    public async Task Add_accepts_positive_amount()
    {
        var (db, pc) = await SeedAsync();
        var page = Page(db);
        page.Amount = 0.5m;
        page.PriceUsdAtTime = 100m;

        await page.OnPostAddAsync(pc.Id);

        Assert.Equal(2, await db.CoinTransactions.CountAsync());
    }

    [Fact]
    public async Task Edit_rejects_zero_amount_and_keeps_the_old_one()
    {
        var (db, pc) = await SeedAsync();
        var page = Page(db);
        page.EditTransactionId = 1;
        page.EditAmount = 0m;
        page.EditPriceUsdAtTime = 100m;

        await page.OnPostEditAsync(pc.Id);

        Assert.Equal(2m, (await db.CoinTransactions.SingleAsync()).Amount);
    }

    [Fact]
    public async Task Repository_rejects_zero_amount()
    {
        var (db, pc) = await SeedAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => new PortfolioRepository(db).AddTransactionAsync(pc.PortfolioId, pc.CoinId, TransactionType.Buy, 0m, 100m));
    }
}
