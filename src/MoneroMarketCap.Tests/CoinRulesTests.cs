using MoneroMarketCap.Data.Helpers;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Data.Repositories;
using MoneroMarketCap.Worker;
using Xunit;

namespace MoneroMarketCap.Tests;

public class CoinRulesTests
{
    [Fact]
    public async Task GetActiveAndGrace_UsesCanonicalOrder_ActiveFirstRankZeroLast()
    {
        using var db = TestDb.Create();
        var now = DateTime.UtcNow;
        db.Coins.AddRange(
            new Coin { Symbol = "AKE", CoinGeckoId = "unranked-active", IsActive = true, MarketCapRank = 0 },
            new Coin { Symbol = "AKE", CoinGeckoId = "grace", IsActive = false, MarketCapRank = 5, LastInTopNUtc = now.AddDays(-3) },
            new Coin { Symbol = "AKE", CoinGeckoId = "ranked-active", IsActive = true, MarketCapRank = 250 },
            new Coin { Symbol = "AKE", CoinGeckoId = "retired", IsActive = false, MarketCapRank = 1, LastInTopNUtc = now.AddDays(-400) });
        await db.SaveChangesAsync();

        var list = await new CoinRepository(db).GetActiveAndGraceAsync(90);

        Assert.Equal(new[] { "ranked-active", "unranked-active", "grace" }, list.Select(c => c.CoinGeckoId));
    }

    [Fact]
    public void IsResolvable_MatchesGraceWindow()
    {
        var cutoff = CoinRules.GraceCutoff(90, new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc));
        Assert.True(CoinRules.IsResolvable(true, null, cutoff));
        Assert.True(CoinRules.IsResolvable(false, cutoff.AddDays(1), cutoff));
        Assert.False(CoinRules.IsResolvable(false, cutoff.AddDays(-1), cutoff));
        Assert.False(CoinRules.IsResolvable(false, null, cutoff));
    }

    [Fact]
    public void TickerMap_PicksSameCoinAsCanonicalOrder()
    {
        var map = CoinTickerMap.Build(new[]
        {
            new CoinTickerMap.CoinTickerRow(1, "AKE", 0, 999m, true),      // active but unranked
            new CoinTickerMap.CoinTickerRow(2, "ake ", 120, 10m, true),    // active, ranked → wins
            new CoinTickerMap.CoinTickerRow(3, "AKE", 3, 1_000_000m, false), // inactive
            new CoinTickerMap.CoinTickerRow(4, "OLD", 7, 1m, false),
            new CoinTickerMap.CoinTickerRow(5, "  ", 1, 1m, true),
        });

        Assert.Equal(2, map["AKE"]);
        Assert.Equal(4, map["old"]);
        Assert.Equal(2, map.Count);
    }
}
