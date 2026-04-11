using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Data.Repositories;
using MoneroMarketCap.Services.Interfaces;
using MoneroMarketCap.Services.Models;

namespace MoneroMarketCap.Pages.Admin;

[Authorize(Policy = "AdminOnly")]
public class IndexModel : PageModel
{
    private readonly ICoinRepository _coins;
    private readonly ICoinGeckoService _gecko;
    private readonly AppDbContext _db;

    public IReadOnlyList<Coin> Coins { get; set; } = new List<Coin>();

    public IndexModel(ICoinRepository coins, ICoinGeckoService gecko, AppDbContext db)
    {
        _coins = coins;
        _gecko = gecko;
        _db = db;
    }

    public async Task OnGetAsync() =>
        Coins = await _coins.GetAllAsync();

    public async Task<IActionResult> OnPostRefreshAllAsync()
    {
        var coins = (await _coins.GetAllAsync())
            .Where(c => c.IsActive && !string.IsNullOrEmpty(c.CoinGeckoId))
            .ToList();

        var data = await _gecko.GetMarketDataBatchAsync(coins.Select(c => c.CoinGeckoId));

        foreach (var coin in coins)
        {
            if (!data.TryGetValue(coin.CoinGeckoId, out var m)) continue;
            coin.PriceUsd = m.CurrentPrice ?? 0;
            coin.MarketCapUsd = m.MarketCap ?? 0;
            coin.CirculatingSupply = m.CirculatingSupply ?? 0;
            coin.PriceChangePercent24h = m.PriceChangePercentage24h ?? 0;
            coin.TotalVolume = m.TotalVolume ?? 0;
            coin.High24h = m.High24h ?? 0;
            coin.Low24h = m.Low24h ?? 0;
            coin.MarketCapRank = m.MarketCapRank ?? 0;
            coin.ImageUrl = m.Image ?? coin.ImageUrl;
            coin.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();

        TempData["Status"] = $"Refreshed {coins.Count} coins.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleActiveAsync(int id)
    {
        var coin = await _coins.GetByIdAsync(id);
        if (coin != null)
        {
            coin.IsActive = !coin.IsActive;
            coin.UpdatedAt = DateTime.UtcNow;
            await _coins.SaveChangesAsync();
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSyncTopCoinsAsync(int count = 500)
    {
        var topCoins = await _gecko.GetTopCoinsAsync(count);
        int added = 0, updated = 0;

        foreach (var m in topCoins)
        {
            var existing = await _db.Coins
                .FirstOrDefaultAsync(c => c.CoinGeckoId == m.Id);

            if (existing == null)
            {
                _db.Coins.Add(MapToCoin(m));
                added++;
            }
            else
            {
                UpdateCoin(existing, m);
                updated++;
            }
        }

        await _db.SaveChangesAsync();
        TempData["Status"] = $"Sync complete — {added} added, {updated} updated.";
        return RedirectToPage();
    }

    private static Coin MapToCoin(CoinGeckoMarketData m) => new()
    {
        CoinGeckoId = m.Id,
        Symbol = m.Symbol.ToUpper(),
        Name = m.Name,
        ImageUrl = m.Image,
        PriceUsd = m.CurrentPrice ?? 0,
        PriceChangePercent24h = m.PriceChangePercentage24h ?? 0,
        High24h = m.High24h ?? 0,
        Low24h = m.Low24h ?? 0,
        MarketCapUsd = m.MarketCap ?? 0,
        MarketCapRank = m.MarketCapRank ?? 0,
        FullyDilutedValuation = m.FullyDilutedValuation ?? 0,
        TotalVolume = m.TotalVolume ?? 0,
        CirculatingSupply = m.CirculatingSupply ?? 0,
        TotalSupply = m.TotalSupply ?? 0,
        MaxSupply = m.MaxSupply,
        Ath = m.Ath ?? 0,
        AthChangePercentage = m.AthChangePercentage ?? 0,
        AthDate = m.AthDate,
        Atl = m.Atl ?? 0,
        AtlChangePercentage = m.AtlChangePercentage ?? 0,
        AtlDate = m.AtlDate,
        IsActive = true
    };

    private static void UpdateCoin(Coin coin, CoinGeckoMarketData m)
    {
        coin.PriceUsd = m.CurrentPrice ?? coin.PriceUsd;
        coin.PriceChangePercent24h = m.PriceChangePercentage24h ?? coin.PriceChangePercent24h;
        coin.High24h = m.High24h ?? coin.High24h;
        coin.Low24h = m.Low24h ?? coin.Low24h;
        coin.MarketCapUsd = m.MarketCap ?? coin.MarketCapUsd;
        coin.MarketCapRank = m.MarketCapRank ?? coin.MarketCapRank;
        coin.FullyDilutedValuation = m.FullyDilutedValuation ?? coin.FullyDilutedValuation;
        coin.TotalVolume = m.TotalVolume ?? coin.TotalVolume;
        coin.CirculatingSupply = m.CirculatingSupply ?? coin.CirculatingSupply;
        coin.TotalSupply = m.TotalSupply ?? coin.TotalSupply;
        coin.MaxSupply = m.MaxSupply ?? coin.MaxSupply;
        coin.ImageUrl = m.Image ?? coin.ImageUrl;
        coin.UpdatedAt = DateTime.UtcNow;
    }
}