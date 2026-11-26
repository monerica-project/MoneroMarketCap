using MoneroMarketCap.Data.Models;
using Xunit;

namespace MoneroMarketCap.Tests;

/// <summary>An exchange is shown only with a listed grade, a live Monerica profile and a website that is not dead.</summary>
public class ExchangeListingRuleTests
{
    private static readonly DateTime Now = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);

    private static Exchange Ex(string? grade = "B", string? monerica = "some-swap", DateTime? downSince = null) =>
        new() { Slug = "some-swap", Name = "Some Swap", Grade = grade, MonericaSlug = monerica, WebsiteDownSinceUtc = downSince };

    [Fact]
    public void GradedExchangeWithMonericaProfileAndWorkingSite_IsListed() => Assert.True(Ex().IsListed(Now));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoMonericaProfile_IsNotListed(string? slug) => Assert.False(Ex(monerica: slug).IsListed(Now));

    [Theory]
    [InlineData("D")]
    [InlineData("F")]
    [InlineData(null)]
    public void LowOrMissingGrade_IsNotListed(string? grade) => Assert.False(Ex(grade: grade).IsListed(Now));

    [Fact]
    public void SiteDownForMoreThanADay_IsNotListed_ButAShortOutageIs()
    {
        Assert.False(Ex(downSince: Now.AddHours(-25)).IsListed(Now));
        Assert.True(Ex(downSince: Now.AddHours(-3)).IsListed(Now));
    }

    [Fact]
    public void QueryRule_MatchesTheInMemoryRule()
    {
        var rule = Exchange.Listed(Now).Compile();
        foreach (var e in new[] { Ex(), Ex(monerica: null), Ex(monerica: ""), Ex(grade: "D"), Ex(downSince: Now.AddHours(-25)), Ex(downSince: Now.AddHours(-3)) })
        {
            Assert.Equal(e.IsListed(Now), rule(e));
        }
    }
}
