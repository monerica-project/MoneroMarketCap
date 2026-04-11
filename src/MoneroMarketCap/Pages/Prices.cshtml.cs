using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MoneroMarketCap.Data.Repositories;

namespace MoneroMarketCap.Pages.Api;

public class PricesModel : PageModel
{
    private readonly ICoinRepository _coins;

    public PricesModel(ICoinRepository coins) => _coins = coins;

    public async Task<IActionResult> OnGetAsync()
    {
        var coins = await _coins.GetAllAsync();
        var result = coins.Select(c => new
        {
            c.Symbol,
            c.Name,
            c.ImageUrl,
            c.PriceUsd,
            c.MarketCapUsd,
            c.MarketCapRank,
            c.CirculatingSupply,
            c.TotalVolume,
            c.PriceChangePercent24h,
            c.PriceChangePercent1h,
            c.PriceChangePercent7d,
            c.PriceChangePercent30d,
            c.High24h,
            c.Low24h,
            c.Ath,
            c.AthChangePercentage,
            c.MaxSupply,
            UpdatedAt = c.UpdatedAt.ToString("HH:mm:ss")
        });
        return new JsonResult(result);
    }
}