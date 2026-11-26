using MoneroMarketCap.Services.Display;
using MoneroMarketCap.Services.Implementations;
using Xunit;

namespace MoneroMarketCap.Tests;

public class FearGreedTests
{
    private static List<(DateTime, decimal)> Series(int days, Func<int, double> price) =>
        Enumerable.Range(0, days).Select(i => (new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i), (decimal)price(i))).ToList();

    [Fact]
    public void NeedsAboutThirteenMonthsOfPrices()
    {
        Assert.Empty(MoneroFearGreed.Compute(Series(MoneroFearGreed.MinDays - 1, i => 100 + i), Series(10, _ => 50000)));
        Assert.Single(MoneroFearGreed.Compute(Series(MoneroFearGreed.MinDays, i => 100 + i), Series(MoneroFearGreed.MinDays, _ => 50000)));
    }

    [Fact]
    public void StrongRallyAtTheHigh_ReadsAsGreed_AndACrash_ReadsAsFear()
    {
        // A flat, slightly wavy year, then a strong month up.
        var up = Series(460, i => i < 430 ? 100 + (3 * Math.Sin(i / 5.0)) : 100 * Math.Pow(1.02, i - 429));
        var flatBtc = Series(460, _ => 50000);
        var greed = MoneroFearGreed.Compute(up, flatBtc).Last().Value;

        var down = Series(460, i => i < 430 ? 100 + (3 * Math.Sin(i / 5.0)) : 100 * Math.Pow(0.97, i - 429));
        var fear = MoneroFearGreed.Compute(down, flatBtc).Last().Value;

        Assert.True(greed >= 70, $"greed {greed}");
        Assert.True(fear <= 25, $"fear {fear}");
    }

    [Fact]
    public void ValuesStayBetween0And100_AndWorkWithoutBitcoinPrices()
    {
        var rng = new Random(7);
        double p = 100;
        var xmr = Series(700, _ => p *= Math.Exp((rng.NextDouble() - 0.5) * 0.12));
        var readings = MoneroFearGreed.Compute(xmr, new List<(DateTime, decimal)>());
        Assert.NotEmpty(readings);
        Assert.All(readings, r => Assert.InRange(r.Value, 0, 100));
    }

    [Theory]
    [InlineData(0, "Extreme Fear")]
    [InlineData(24, "Extreme Fear")]
    [InlineData(25, "Fear")]
    [InlineData(50, "Neutral")]
    [InlineData(60, "Greed")]
    [InlineData(90, "Extreme Greed")]
    public void Labels(int value, string expected) => Assert.Equal(expected, MoneroFearGreed.Label(value));

    [Fact]
    public void ParsesTheMarketIndexAnswer_AndSkipsBadRows()
    {
        const string json = """{"name":"Fear and Greed Index","data":[{"value":"13","value_classification":"Extreme Fear","timestamp":"1795564800"},{"value":"x","timestamp":"1795478400"},{"value":"140","timestamp":"1795392000"}],"metadata":{"error":null}}""";
        var rows = FearGreedService.ParseMarket(json);
        var row = Assert.Single(rows);
        Assert.Equal(13, row.Value);
        Assert.Equal(TimeSpan.Zero, row.Date.TimeOfDay);
    }
}
