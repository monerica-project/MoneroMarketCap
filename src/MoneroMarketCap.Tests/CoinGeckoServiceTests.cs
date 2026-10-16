using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MoneroMarketCap.Services.Implementations;
using Xunit;

namespace MoneroMarketCap.Tests;

public class CoinGeckoServiceTests
{
    private static string Page(int start, int count) =>
        "[" + string.Join(",", Enumerable.Range(start, count).Select(i =>
            $"{{\"id\":\"c{i}\",\"symbol\":\"s{i}\",\"name\":\"n{i}\",\"market_cap_rank\":{i}}}")) + "]";

    private static CoinGeckoService Service(FakeHandler handler) =>
        new(new HttpClient(handler), NullLogger<CoinGeckoService>.Instance, TestDb.Config());

    [Fact]
    public async Task TopCoins_FailedLaterPage_IsFlaggedIncomplete()
    {
        var handler = new FakeHandler((req, _) => Task.FromResult(
            req.RequestUri!.Query.Contains("page=1&")
                ? FakeHandler.Text(Page(1, 250), "application/json")
                : FakeHandler.Text("rate limited", "text/plain", HttpStatusCode.TooManyRequests)));

        var result = await Service(handler).GetTopCoinsAsync(300);

        Assert.False(result.IsComplete);
        Assert.Equal(250, result.Coins.Count);
    }

    [Fact]
    public async Task TopCoins_AllPagesOk_IsComplete()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(FakeHandler.Text(Page(1, 250), "application/json")));

        var result = await Service(handler).GetTopCoinsAsync(100);

        Assert.True(result.IsComplete);
        Assert.Equal(100, result.Coins.Count);
    }
}
