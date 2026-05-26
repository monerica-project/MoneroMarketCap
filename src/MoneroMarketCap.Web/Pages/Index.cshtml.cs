using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Data.Repositories;
using MoneroMarketCap.Services.Interfaces;
using MoneroMarketCap.Services.Models;
using MoneroMarketCap.Web.Helpers;

namespace MoneroMarketCap.Pages;

public class IndexModel : PageModel
{
    private readonly ICoinRepository _coins;
    private readonly IFiatRateService _fxRates;
    private readonly IConfiguration _config;

    public int SponsorRotateIntervalSeconds { get; set; }
    public IReadOnlyList<Coin> Coins { get; set; } = new List<Coin>();
    public Coin? Monero { get; set; }

    /// <summary>The currency currently being displayed.</summary>
    public CurrencyInfo Currency { get; set; } = CurrencyCatalog.Default;

    /// <summary>How many units of <see cref="Currency"/> equal 1 USD.</summary>
    public decimal RatePerUsd { get; set; } = 1m;

    public IndexModel(
        ICoinRepository coins,
        IFiatRateService fxRates,
        IConfiguration config)
    {
        _coins = coins;
        _fxRates = fxRates;
        _config = config;
    }

    public async Task OnGetAsync()
    {
        Coins = await _coins.GetAllAsync();
        Monero = await _coins.GetByCoinGeckoIdAsync("monero");
        SponsorRotateIntervalSeconds = _config.GetValue<int>("Sponsors:RotateIntervalSeconds", 30);

        Currency = CurrencyResolver.Resolve(HttpContext);
        var rates = await _fxRates.GetRatesAsync(HttpContext.RequestAborted);
        RatePerUsd = rates.TryGetValue(Currency.Code, out var r) && r > 0 ? r : 1m;
    }
}
