namespace MoneroMarketCap.Services.Models;

/// <summary>
/// Result of a paged top-N fetch. <see cref="IsComplete"/> is false when a page request
/// failed part-way, so <see cref="Coins"/> holds only the pages that did arrive. Callers
/// must NOT treat an incomplete list as the authoritative top N (e.g. must not deactivate
/// coins merely because they are missing from it); they may still use the prices received.
/// </summary>
public sealed class TopCoinsResult
{
    public TopCoinsResult(List<CoinGeckoMarketData> coins, bool isComplete)
    {
        this.Coins = coins;
        this.IsComplete = isComplete;
    }

    public List<CoinGeckoMarketData> Coins { get; }

    public bool IsComplete { get; }
}
