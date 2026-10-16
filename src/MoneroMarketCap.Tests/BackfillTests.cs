using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Services.Implementations;
using Xunit;

namespace MoneroMarketCap.Tests;

public class BackfillTests
{
    private static string ChartJson(params DateTime[] days) =>
        "[" + string.Join(",", days.Select(d =>
            $"[{new DateTimeOffset(d, TimeSpan.Zero).ToUnixTimeMilliseconds()},1.5]")) + "]";

    private static async Task<(CoinHistoryBackfillService Svc, ServiceProvider Sp, int CoinId)> BuildAsync(string name, FakeGecko gecko, DateTime? stamp = null)
    {
        var sp = TestDb.Services(name, gecko);
        var svc = new CoinHistoryBackfillService(
            sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<CoinHistoryBackfillService>.Instance, TestDb.Config());
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var coin = new Coin { CoinGeckoId = "monero", Symbol = "XMR", IsActive = true, DailyHistoryBackfilledAtUtc = stamp };
        db.Coins.Add(coin);
        await db.SaveChangesAsync();
        return (svc, sp, coin.Id);
    }

    [Fact]
    public async Task ConcurrentBackfills_OfSameCoin_FetchAndInsertOnce()
    {
        var today = DateTime.UtcNow.Date;
        var gecko = new FakeGecko
        {
            ChartJson = ChartJson(today.AddDays(-2), today.AddDays(-1), today),
            ChartDelay = TimeSpan.FromMilliseconds(200),
        };
        var (svc, sp, coinId) = await BuildAsync(nameof(ConcurrentBackfills_OfSameCoin_FetchAndInsertOnce), gecko);

        // Startup pass (skip if stamped at all) racing the per-cycle pass.
        var a = svc.BackfillCoinAsync(coinId, DateTime.MinValue);
        var b = svc.BackfillCoinAsync(coinId, DateTime.UtcNow.AddDays(-30));
        var results = await Task.WhenAll(a, b);

        Assert.Equal(1, gecko.ChartCalls);
        Assert.Contains(results, r => r.Status == BackfillStatus.NotDue);

        using var scope = sp.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<AppDbContext>().CoinPriceHistories
            .Where(h => h.CoinId == coinId && h.Interval == "1d").ToListAsync();

        // Two past days; today is left to the price worker, which owns today's row.
        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, r => r.RecordedAt == today);
    }

    [Fact]
    public async Task Backfill_SkipsDaysAlreadyPresent()
    {
        var today = DateTime.UtcNow.Date;
        var gecko = new FakeGecko { ChartJson = ChartJson(today.AddDays(-3), today.AddDays(-2)) };
        var (svc, sp, coinId) = await BuildAsync(nameof(Backfill_SkipsDaysAlreadyPresent), gecko);
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.CoinPriceHistories.Add(new CoinPriceHistory { CoinId = coinId, Interval = "1d", RecordedAt = today.AddDays(-3) });
            await db.SaveChangesAsync();
        }

        var result = await svc.BackfillCoinAsync(coinId);

        Assert.Equal(BackfillStatus.Inserted, result.Status);
        Assert.Equal(1, result.RowsInserted);
    }

    [Fact]
    public async Task Backfill_RecentlyStamped_IsNotDue()
    {
        var gecko = new FakeGecko { ChartJson = "[]" };
        var (svc, _, coinId) = await BuildAsync(nameof(Backfill_RecentlyStamped_IsNotDue), gecko, stamp: DateTime.UtcNow);

        var result = await svc.BackfillCoinAsync(coinId, DateTime.UtcNow.AddDays(-30));

        Assert.Equal(BackfillStatus.NotDue, result.Status);
        Assert.Equal(0, gecko.ChartCalls);
    }
}
