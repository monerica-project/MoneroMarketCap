using Microsoft.AspNetCore.Mvc.RazorPages;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Data.Repositories;
using MoneroMarketCap.Services.Interfaces;
using MoneroMarketCap.Services.Models;
using MoneroMarketCap.Web.Helpers;

namespace MoneroMarketCap.Pages.Coins;

public class IndexModel : PageModel
{
    private readonly ICoinRepository _coins;
    private readonly IFiatRateService _fxRates;

    public IReadOnlyList<Coin> AllCoins { get; set; } = new List<Coin>();
    public CurrencyInfo Currency { get; set; } = CurrencyCatalog.Default;
    public decimal RatePerUsd { get; set; } = 1m;

    public IndexModel(ICoinRepository coins, IFiatRateService fxRates)
    {
        _coins = coins;
        _fxRates = fxRates;
    }

    public async Task OnGetAsync()
    {
        AllCoins = await _coins.GetAllAsync();

        Currency = CurrencyResolver.Resolve(HttpContext);
        var rates = await _fxRates.GetRatesAsync(HttpContext.RequestAborted);
        RatePerUsd = rates.TryGetValue(Currency.Code, out var r) && r > 0 ? r : 1m;
    }
}
