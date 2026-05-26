using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MoneroMarketCap.Data.Repositories;
using MoneroMarketCap.Services.Interfaces;
using MoneroMarketCap.Services.Models;
using MoneroMarketCap.Web.Helpers;

namespace MoneroMarketCap.Pages.Api;

public class PricesModel : PageModel
{
    private readonly ICoinRepository _coins;
    private readonly IFiatRateService _fxRates;

    public PricesModel(ICoinRepository coins, IFiatRateService fxRates)
    {
        _coins = coins;
        _fxRates = fxRates;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var coins = await _coins.GetAllAsync();

        var currency = CurrencyResolver.Resolve(HttpContext);
        var rates = await _fxRates.GetRatesAsync(HttpContext.RequestAborted);
        var rate = rates.TryGetValue(currency.Code, out var r) && r > 0 ? r : 1m;

        var coinData = coins.Select(c => new
        {
            c.Symbol,
            c.Name,
            c.ImageUrl,
            // priceUsd stays canonical so the JS XMR-ratio math (currency-invariant)
            // and the supply * priceUsd effective-mcap math both still work.
            c.PriceUsd,
            c.MarketCapUsd,
            c.MarketCapRank,
            c.CirculatingSupply,
            c.TotalVolume,
            c.PriceChangePercent24h,
            c.PriceChangePercent1h,
            c.PriceChangePercent7d,
            c.PriceChangePercent30d,
            c.PriceChangePercent1y,
            c.High24h,
            c.Low24h,
            c.Ath,
            c.AthChangePercentage,
            c.MaxSupply,
            UpdatedAt = c.UpdatedAt.ToString("HH:mm:ss"),
        });

        return new JsonResult(new
        {
            currency = new
            {
                code = currency.Code,
                symbol = currency.Symbol,
                before = currency.SymbolBefore,
                decimals = currency.Decimals,
                ratePerUsd = rate,
            },
            coins = coinData,
        });
    }
}
