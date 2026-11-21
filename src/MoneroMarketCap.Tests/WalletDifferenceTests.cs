using MoneroMarketCap.Data.Helpers;
using MoneroMarketCap.Data.Models;
using Xunit;

namespace MoneroMarketCap.Tests;

public class WalletDifferenceTests
{
    [Fact]
    public void MoreInWalletThanTracked_IsABuyOfTheGap()
    {
        var r = WalletDifference.Compute(trackedBalance: 10m, walletBalance: 12.5m);
        Assert.Equal((TransactionType.Buy, 2.5m), r);
    }

    [Fact]
    public void LessInWalletThanTracked_IsASellOfTheGap()
    {
        var r = WalletDifference.Compute(trackedBalance: 10m, walletBalance: 4m);
        Assert.Equal((TransactionType.Sell, 6m), r);
    }

    [Fact]
    public void EmptyWallet_SellsEverythingTracked()
    {
        Assert.Equal((TransactionType.Sell, 3.2m), WalletDifference.Compute(3.2m, 0m));
    }

    [Fact]
    public void SameBalance_NeedsNothing()
    {
        Assert.Null(WalletDifference.Compute(7.25m, 7.25m));
    }

    [Fact]
    public void ApplyingTheResult_MakesTheTrackedBalanceMatchTheWallet()
    {
        var coin = new PortfolioCoin();
        var txs = new List<CoinTransaction>
        {
            new() { Type = TransactionType.Buy, Amount = 10m, PriceUsdAtTime = 100m, TransactedAt = DateTime.UtcNow.AddDays(-2) },
        };
        var (type, amount) = WalletDifference.Compute(10m, 6.123456789012m)!.Value;
        txs.Add(new CoinTransaction { Type = type, Amount = amount, PriceUsdAtTime = 150m, TransactedAt = DateTime.UtcNow });

        PortfolioTotalsCalculator.Apply(coin, txs);
        Assert.Equal(6.123456789012m, coin.TotalAmount);
    }

    [Theory]
    [InlineData("12.5", 12.5)]
    [InlineData(" 1,234.56 ", 1234.56)]
    [InlineData("0", 0)]
    public void TryParseBalance_ReadsPlainNumbers(string text, double expected)
    {
        Assert.True(WalletDifference.TryParseBalance(text, out var balance));
        Assert.Equal(Math.Round((decimal)expected, 8), balance);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("1e9")]
    [InlineData("99999999999999999999")]
    public void TryParseBalance_RejectsEverythingElse(string? text)
    {
        Assert.False(WalletDifference.TryParseBalance(text, out _));
    }
}
