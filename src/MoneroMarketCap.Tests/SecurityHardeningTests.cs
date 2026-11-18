using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Helpers;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Data.Repositories;
using MoneroMarketCap.Pages.Portfolios;
using MoneroMarketCap.Web.Services;
using Xunit;

namespace MoneroMarketCap.Tests;

// Portfolio caps, explorer call budgets and not-found cache, safe outbound links,
// Digest-only node login and the bounded captcha store.
public class SecurityHardeningTests
{
    private sealed class NoTempDataStore : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }

    private static DefaultHttpContext Http(int userId) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "test")),
    };

    private static DetailModel DetailPage(AppDbContext db, int userId = 1)
    {
        var http = Http(userId);
        return new DetailModel(new PortfolioRepository(db), new CoinRepository(db), db, null!)
        {
            PageContext = new Microsoft.AspNetCore.Mvc.RazorPages.PageContext { HttpContext = http },
            TempData = new TempDataDictionary(http, new NoTempDataStore()),
        };
    }

    private static TransactionsModel TransactionsPage(AppDbContext db, int userId = 1)
    {
        var http = Http(userId);
        return new TransactionsModel(db, new PortfolioRepository(db))
        {
            PageContext = new Microsoft.AspNetCore.Mvc.RazorPages.PageContext { HttpContext = http },
            TempData = new TempDataDictionary(http, new NoTempDataStore()),
        };
    }

    private static Coin NewCoin(string symbol) => new() { Symbol = symbol, Name = symbol, CoinGeckoId = symbol.ToLowerInvariant() };

    [Fact]
    public async Task AddTransaction_RejectsCoinIdThatDoesNotExist()
    {
        var db = TestDb.Create();
        var portfolio = new Portfolio { UserId = 1 };
        db.Portfolios.Add(portfolio);
        db.Coins.Add(NewCoin("XMR"));
        await db.SaveChangesAsync();

        var page = DetailPage(db);
        page.CoinId = 999_999;
        page.Amount = 1m;
        page.PriceUsdAtTime = 100m;

        var result = await page.OnPostAddTransactionAsync(portfolio.Id);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal(PortfolioLimits.UnknownCoinMessage, page.TempData["ImportMessage"]);
        Assert.Equal(0, await db.PortfolioCoins.CountAsync());
        Assert.Equal(0, await db.CoinTransactions.CountAsync());
    }

    [Fact]
    public async Task AddTransaction_AcceptsRealCoin()
    {
        var db = TestDb.Create();
        var portfolio = new Portfolio { UserId = 1 };
        var coin = NewCoin("XMR");
        db.Portfolios.Add(portfolio);
        db.Coins.Add(coin);
        await db.SaveChangesAsync();

        var page = DetailPage(db);
        page.CoinId = coin.Id;
        page.Amount = 1m;
        page.PriceUsdAtTime = 100m;

        await page.OnPostAddTransactionAsync(portfolio.Id);

        Assert.Equal(1, await db.CoinTransactions.CountAsync());
    }

    [Fact]
    public async Task AddTransaction_StopsAtCoinsPerPortfolioCap_ButStillAllowsHeldCoins()
    {
        var db = TestDb.Create();
        var portfolio = new Portfolio { UserId = 1 };
        db.Portfolios.Add(portfolio);
        for (var i = 0; i < PortfolioLimits.MaxCoinsPerPortfolio; i++)
        {
            portfolio.PortfolioCoins.Add(new PortfolioCoin { Coin = NewCoin("C" + i) });
        }

        var extra = NewCoin("NEW");
        db.Coins.Add(extra);
        await db.SaveChangesAsync();

        var page = DetailPage(db);
        page.CoinId = extra.Id;
        page.Amount = 1m;
        page.PriceUsdAtTime = 1m;
        await page.OnPostAddTransactionAsync(portfolio.Id);

        Assert.Equal(PortfolioLimits.CoinLimitMessage, page.TempData["ImportMessage"]);
        Assert.Equal(PortfolioLimits.MaxCoinsPerPortfolio, await db.PortfolioCoins.CountAsync());

        // A coin the portfolio already holds can still get transactions.
        var held = await db.PortfolioCoins.Select(pc => pc.CoinId).FirstAsync();
        var page2 = DetailPage(db);
        page2.CoinId = held;
        page2.Amount = 1m;
        page2.PriceUsdAtTime = 1m;
        await page2.OnPostAddTransactionAsync(portfolio.Id);

        Assert.Equal(1, await db.CoinTransactions.CountAsync());
    }

    [Fact]
    public async Task UserTransactionCap_CountsAcrossPortfolios_AndBlocksAdds()
    {
        var db = TestDb.Create();
        var coin = NewCoin("XMR");

        // The member's transactions sit in five coin rows over two portfolios, each row below
        // the 5,000 per-coin cap, so only the account-wide cap can refuse the next one.
        var first = new Portfolio { UserId = 1 };
        var second = new Portfolio { UserId = 1 };
        var full = Enumerable.Range(0, 5)
            .Select(i => new PortfolioCoin { Portfolio = i < 3 ? first : second, Coin = i == 0 ? coin : NewCoin("F" + i) })
            .ToList();
        var target = new PortfolioCoin { Portfolio = second, Coin = NewCoin("EMPTY") };
        var other = new PortfolioCoin { Portfolio = new Portfolio { UserId = 2 }, Coin = coin };
        db.PortfolioCoins.AddRange(full);
        db.PortfolioCoins.AddRange(target, other);

        var perRow = PortfolioLimits.MaxTransactionsPerUser / full.Count;
        Assert.True(perRow < SafetyLimits.MaxTransactionsPerPortfolioCoin);
        for (var i = 0; i < PortfolioLimits.MaxTransactionsPerUser; i++)
        {
            db.CoinTransactions.Add(new CoinTransaction
            {
                PortfolioCoin = full[i / perRow],
                Type = TransactionType.Buy,
                Amount = 1m,
                PriceUsdAtTime = 1m,
                TransactedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();

        Assert.Equal(PortfolioLimits.MaxTransactionsPerUser, await PortfolioLimits.CountUserTransactionsAsync(db, 1));
        Assert.Equal(0, await PortfolioLimits.UserRoomLeftAsync(db, 1));
        Assert.Equal(PortfolioLimits.MaxTransactionsPerUser, await PortfolioLimits.UserRoomLeftAsync(db, 2));

        var page = TransactionsPage(db);
        page.Amount = 1m;
        page.PriceUsdAtTime = 1m;
        await page.OnPostAddAsync(target.Id);

        Assert.Equal(PortfolioLimits.UserTransactionLimitMessage, page.TempData["ImportMessage"]);
        Assert.Equal(PortfolioLimits.MaxTransactionsPerUser, await db.CoinTransactions.CountAsync());

        // Another member is not affected.
        var otherPage = TransactionsPage(db, userId: 2);
        otherPage.Amount = 1m;
        otherPage.PriceUsdAtTime = 1m;
        await otherPage.OnPostAddAsync(other.Id);
        Assert.Equal(PortfolioLimits.MaxTransactionsPerUser + 1, await db.CoinTransactions.CountAsync());
    }

    [Theory]
    [InlineData("https://example.com/x", "https://example.com/x")]
    [InlineData("  http://example.com  ", "http://example.com")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("JAVASCRIPT:alert(1)", null)]
    [InlineData("data:text/html,hi", null)]
    [InlineData("//example.com", null)]
    [InlineData("/relative", null)]
    [InlineData("ftp://example.com", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void SafeUrl_KeepsOnlyAbsoluteHttpLinks(string? input, string? expected)
    {
        Assert.Equal(expected, SafeUrl.HttpOrNull(input));
        Assert.Equal(expected ?? string.Empty, SafeUrl.HttpOrEmpty(input));
    }

    [Fact]
    public void NodeCredentials_AnswerDigestOnly()
    {
        var creds = MoneroNodeCredentials.DigestOnly("http://127.0.0.1:18089/", "user", "secret");
        Assert.NotNull(creds);
        var node = new Uri("http://127.0.0.1:18089/json_rpc");

        Assert.NotNull(creds!.GetCredential(node, "Digest"));
        Assert.Null(creds.GetCredential(node, "Basic"));
        Assert.Null(creds.GetCredential(new Uri("http://evil.example/json_rpc"), "Digest"));
        Assert.Null(MoneroNodeCredentials.DigestOnly(null, "user", "secret"));
    }

    [Fact]
    public void NotFoundCache_ExpiresAfterTtl()
    {
        var cache = new ExplorerNotFoundCache(10, TimeSpan.FromSeconds(60));
        var t0 = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

        cache.Add("a", t0);
        Assert.True(cache.Contains("a", t0.AddSeconds(59)));
        Assert.False(cache.Contains("a", t0.AddSeconds(60)));
    }

    [Fact]
    public void NotFoundCache_IsBounded_DropsOldest()
    {
        var cache = new ExplorerNotFoundCache(3, TimeSpan.FromSeconds(60));
        var t0 = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < 1000; i++)
        {
            cache.Add("k" + i, t0.AddMilliseconds(i));
        }

        Assert.Equal(3, cache.Count);
        Assert.False(cache.Contains("k0", t0.AddSeconds(1)));
        Assert.True(cache.Contains("k999", t0.AddSeconds(1)));
    }

    [Fact]
    public void CallBudget_RefusesPastLimit_AndRefillsAfterAMinute()
    {
        var budget = new ExplorerCallBudget(2);
        var t0 = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(budget.TryTake(t0));
        Assert.True(budget.TryTake(t0));
        Assert.False(budget.TryTake(t0.AddSeconds(30)));
        Assert.True(budget.TryTake(t0.AddSeconds(61)));
    }

    [Fact]
    public void ExplorerBudgets_AreSplit_AndTogetherMatchTheOldCeiling()
    {
        Assert.True(MoneroExplorer.HeadlineCallsPerMinute > 0);
        Assert.True(MoneroExplorer.LookupCallsPerMinute > 0);
        Assert.Equal(240, MoneroExplorer.HeadlineCallsPerMinute + MoneroExplorer.LookupCallsPerMinute);
    }

    [Fact]
    public async Task Explorer_RemembersUnknownTransaction_SoRepeatsCostNoNodeCall()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(
            FakeHandler.Text("{\"missed_tx\":[\"x\"],\"status\":\"OK\"}", "application/json")));
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://node.test/") };
        var explorer = new MoneroExplorer(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<MoneroExplorer>.Instance);

        // A random hash so other tests (the not-found store is shared) never collide with it.
        var hash = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        Assert.Null(await explorer.GetTransactionAsync(hash, CancellationToken.None));
        Assert.Null(await explorer.GetTransactionAsync(hash, CancellationToken.None));
        Assert.Null(await explorer.GetTransactionAsync(hash.ToLowerInvariant(), CancellationToken.None));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Explorer_DoesNotRememberNodeFailures()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(
            FakeHandler.Text("down", "text/plain", HttpStatusCode.BadGateway)));
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://node.test/") };
        var explorer = new MoneroExplorer(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<MoneroExplorer>.Instance);
        var hash = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        Assert.Null(await explorer.GetTransactionAsync(hash, CancellationToken.None));
        Assert.Null(await explorer.GetTransactionAsync(hash, CancellationToken.None));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public void Captcha_IsBounded_OldestTokenDroppedWhenFull()
    {
        var store = new CaptchaStore(new MemoryCache(new MemoryCacheOptions()), maxTokens: 2);

        var first = store.Create(1);
        var second = store.Create(2);
        var third = store.Create(3);

        Assert.False(store.TryConsume(first, 1));
        Assert.True(store.TryConsume(second, 2));
        Assert.True(store.TryConsume(third, 3));
    }
}
