using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Services.Implementations;
using MoneroMarketCap.Services.Models;
using Xunit;

namespace MoneroMarketCap.Tests;

public class PriceWorkerTests
{
    private static (CoinPriceUpdateService Svc, ServiceProvider Sp) Build(string dbName, FakeGecko gecko)
    {
        var sp = TestDb.Services(dbName, gecko);
        var scopes = sp.GetRequiredService<IServiceScopeFactory>();
        var config = TestDb.Config(new() { ["CoinGecko:TopCount"] = "3" });
        var backfill = new CoinHistoryBackfillService(scopes, NullLogger<CoinHistoryBackfillService>.Instance, config);
        var svc = new CoinPriceUpdateService(scopes, NullLogger<CoinPriceUpdateService>.Instance, backfill, config);
        return (svc, sp);
    }

    private static async Task SeedAsync(ServiceProvider sp, params Coin[] coins)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Coins.AddRange(coins);
        await db.SaveChangesAsync();
    }

    private static async Task<List<Coin>> CoinsAsync(ServiceProvider sp)
    {
        using var scope = sp.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Coins.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task IncompleteTopList_PricesReceivedCoins_ButDeactivatesNothing()
    {
        var gecko = new FakeGecko
        {
            Top = new TopCoinsResult(new() { FakeGecko.Market("bitcoin", "btc", 1, 50_000m) }, isComplete: false),
        };
        var (svc, sp) = Build(nameof(IncompleteTopList_PricesReceivedCoins_ButDeactivatesNothing), gecko);
        var stamp = DateTime.UtcNow.AddDays(-5);
        await SeedAsync(sp,
            new Coin { CoinGeckoId = "bitcoin", Symbol = "BTC", IsActive = true, DailyHistoryBackfilledAtUtc = stamp },
            new Coin { CoinGeckoId = "rank-280", Symbol = "R", IsActive = true, DailyHistoryBackfilledAtUtc = stamp });

        await svc.UpdatePricesAsync();

        var coins = await CoinsAsync(sp);
        var btc = coins.Single(c => c.CoinGeckoId == "bitcoin");
        var other = coins.Single(c => c.CoinGeckoId == "rank-280");
        Assert.Equal(50_000m, btc.PriceUsd);
        Assert.NotNull(btc.PriceUpdatedAtUtc);
        Assert.True(other.IsActive);                       // not deactivated on a partial list
        Assert.Equal(stamp, other.DailyHistoryBackfilledAtUtc); // backfill stamp untouched
    }

    [Fact]
    public async Task CompleteTopList_RespectsManualOverrides_AndPricesManualCoinsById()
    {
        var gecko = new FakeGecko
        {
            Top = new TopCoinsResult(new()
            {
                FakeGecko.Market("bitcoin", "btc", 1, 50_000m),
                FakeGecko.Market("hidden", "hid", 2, 5m),
            }, isComplete: true),
        };
        gecko.Batch["wownero"] = FakeGecko.Market("wownero", "wow", 900, 0.05m);
        var (svc, sp) = Build(nameof(CompleteTopList_RespectsManualOverrides_AndPricesManualCoinsById), gecko);
        await SeedAsync(sp,
            new Coin { CoinGeckoId = "bitcoin", Symbol = "BTC", IsActive = true },
            new Coin { CoinGeckoId = "hidden", Symbol = "HID", IsActive = false, IsActiveOverride = false },
            new Coin { CoinGeckoId = "wownero", Symbol = "WOW", IsActive = true, IsActiveOverride = true, PriceUsd = 0.01m },
            new Coin { CoinGeckoId = "dropped", Symbol = "DRP", IsActive = true });

        await svc.UpdatePricesAsync();

        var coins = (await CoinsAsync(sp)).ToDictionary(c => c.CoinGeckoId);
        Assert.False(coins["hidden"].IsActive);   // pinned hidden stays hidden in the top N
        Assert.True(coins["wownero"].IsActive);   // pinned active survives outside the top N
        Assert.Equal(0.05m, coins["wownero"].PriceUsd);
        Assert.NotNull(coins["wownero"].PriceUpdatedAtUtc);
        Assert.False(coins["dropped"].IsActive);  // automatic coin outside a complete list
        Assert.Contains(gecko.BatchRequests, r => r.SequenceEqual(new[] { "wownero" }));

        // Manual coin also gets today's daily history row.
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.CoinPriceHistories.AnyAsync(h => h.CoinId == coins["wownero"].Id && h.Interval == "1d"));
    }

    [Fact]
    public async Task TodaysDuplicateDailyRows_DoNotThrow_AndLowestIdIsUpdated()
    {
        var gecko = new FakeGecko
        {
            Top = new TopCoinsResult(new() { FakeGecko.Market("bitcoin", "btc", 1, 60_000m) }, isComplete: true),
        };
        var (svc, sp) = Build(nameof(TodaysDuplicateDailyRows_DoNotThrow_AndLowestIdIsUpdated), gecko);
        var btc = new Coin { CoinGeckoId = "bitcoin", Symbol = "BTC", IsActive = true, DailyHistoryBackfilledAtUtc = DateTime.UtcNow };
        await SeedAsync(sp, btc);
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var today = DateTime.UtcNow.Date;
            db.CoinPriceHistories.AddRange(
                new CoinPriceHistory { Id = 10, CoinId = btc.Id, Interval = "1d", RecordedAt = today, PriceUsd = 1m },
                new CoinPriceHistory { Id = 11, CoinId = btc.Id, Interval = "1d", RecordedAt = today, PriceUsd = 2m });
            await db.SaveChangesAsync();
        }

        await svc.UpdatePricesAsync();

        using var check = sp.CreateScope();
        var rows = await check.ServiceProvider.GetRequiredService<AppDbContext>().CoinPriceHistories
            .Where(h => h.Interval == "1d").OrderBy(h => h.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(60_000m, rows[0].PriceUsd);
    }

    [Fact]
    public void PickDailyRowPerCoin_KeepsLowestId()
    {
        var picked = CoinPriceUpdateService.PickDailyRowPerCoin(new[]
        {
            new CoinPriceHistory { Id = 7, CoinId = 1 },
            new CoinPriceHistory { Id = 3, CoinId = 1 },
            new CoinPriceHistory { Id = 5, CoinId = 2 },
        });

        Assert.Equal(3, picked[1].Id);
        Assert.Equal(5, picked[2].Id);
    }
}
