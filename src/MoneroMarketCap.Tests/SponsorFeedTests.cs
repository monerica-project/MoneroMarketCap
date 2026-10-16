using System.Diagnostics;
using System.Net;
using MoneroMarketCap.Web.Services;
using Xunit;

namespace MoneroMarketCap.Tests;

public class SponsorFeedTests
{
    private const string Json = "[{\"name\":\"Acme\",\"link\":\"https://acme.example\",\"sponsorshipType\":\"MainSponsor\"}]";

    private static SponsorFeed Feed(FakeHandler handler, TimeProvider? clock = null) =>
        new(new FakeHttpClientFactory(handler),
            TestDb.Config(new() { ["Sponsors:SourceUrl"] = "https://feed.example/sponsors", ["Sponsors:CacheTtlMinutes"] = "60" }),
            clock);

    [Fact]
    public void TryParseFeed_RejectsHtmlAndNonArrays()
    {
        Assert.Null(SponsorFeed.TryParseFeed("text/html", "<html>Bad gateway</html>"));
        Assert.Null(SponsorFeed.TryParseFeed("application/json", "<html>oops</html>"));
        Assert.Null(SponsorFeed.TryParseFeed("application/json", "{\"error\":1}"));
        Assert.Null(SponsorFeed.TryParseFeed("application/json", ""));
        Assert.Single(SponsorFeed.TryParseFeed("application/json", Json)!);
    }

    [Fact]
    public async Task Html200_IsNotCachedOrServed()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(FakeHandler.Text("<html>maintenance</html>", "text/html")));
        var feed = Feed(handler);

        Assert.Equal(string.Empty, await feed.GetRawJsonAsync());
        Assert.Empty(await feed.GetSponsorsAsync());
    }

    [Fact]
    public async Task Failure_BacksOff_InsteadOfRetryingEveryRequest()
    {
        var clock = new ManualClock();
        var handler = new FakeHandler((_, _) => Task.FromResult(FakeHandler.Text("err", "text/plain", HttpStatusCode.BadGateway)));
        var feed = Feed(handler, clock);

        await feed.GetRawJsonAsync();
        await feed.GetRawJsonAsync();
        await feed.GetRawJsonAsync();
        Assert.Equal(1, handler.Calls);

        clock.Advance(SponsorFeed.RetryAfterFailure + TimeSpan.FromSeconds(1));
        await feed.GetRawJsonAsync();
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task SlowUpstream_NeverStallsRenderingBeyondMaxWait()
    {
        var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(8), ct);
            return FakeHandler.Text(Json, "application/json");
        });
        var feed = Feed(handler);

        var sw = Stopwatch.StartNew();
        var items = await feed.GetSponsorsAsync();
        sw.Stop();

        Assert.Empty(items);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task StaleData_IsServedImmediately_WhileRefreshingInBackground()
    {
        var clock = new ManualClock();
        var slow = false;
        var handler = new FakeHandler(async (_, ct) =>
        {
            if (slow)
            {
                await Task.Delay(TimeSpan.FromSeconds(8), ct);
            }

            return FakeHandler.Text(Json, "application/json") /* StringContent adds charset=utf-8 */;
        });
        var feed = Feed(handler, clock);

        Assert.Single(await feed.GetSponsorsAsync());

        slow = true;
        clock.Advance(TimeSpan.FromMinutes(61));
        var sw = Stopwatch.StartNew();
        var items = await feed.GetSponsorsAsync();
        sw.Stop();

        Assert.Single(items); // last good data, no waiting
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"took {sw.Elapsed}");
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => this.now;

        public void Advance(TimeSpan by) => this.now += by;
    }
}
