namespace MoneroMarketCap.Services.Models;

public class CoinGeckoSearchResult
{
    public string Id { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Thumb { get; set; }
}