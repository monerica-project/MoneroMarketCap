// Services/Display/MoneroSupplyDisplay.cs
using MoneroMarketCap.Data.Models;

namespace MoneroMarketCap.Services.Display;

public static class MoneroSupplyDisplay
{
    // Block reward is 0.6 XMR every ~2 min. 30 min staleness = ~9 XMR max drift.
    private static readonly TimeSpan FreshnessWindow = TimeSpan.FromMinutes(30);

    public static bool IsNodeSupplyFresh(Coin coin) =>
        coin.NodeSupply is not null
        && coin.NodeSupplyUpdatedAt is { } ts
        && DateTime.UtcNow - ts < FreshnessWindow;

    public static decimal? NodeMarketCapUsd(Coin coin) =>
        coin.NodeSupply is { } supply && coin.PriceUsd > 0
            ? supply * coin.PriceUsd
            : null;

    public static string FormatAgo(DateTime? utc)
    {
        if (utc is null) return "never";
        var delta = DateTime.UtcNow - utc.Value;
        if (delta.TotalSeconds < 60) return "just now";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes} min ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours} hr ago";
        return $"{(int)delta.TotalDays} d ago";
    }
}