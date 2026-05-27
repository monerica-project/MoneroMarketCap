using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Data.Repositories;
using MoneroMarketCap.Services.Interfaces;
using MoneroMarketCap.Services.Models;
using MoneroMarketCap.Web.Helpers;

namespace MoneroMarketCap.Pages.Coins;

public class DetailModel : PageModel
{
    private readonly ICoinRepository _coins;
    private readonly IFiatRateService _fxRates;

    public Coin? Coin { get; set; }
    public Coin? Monero { get; set; }

    public decimal? VsXmrChange24h { get; set; }
    public decimal? VsXmrChange7d { get; set; }
    public decimal? VsXmrChange30d { get; set; }
    public decimal? VsXmrChange1yr { get; set; }

    public CurrencyInfo Currency { get; set; } = CurrencyCatalog.Default;
    public decimal RatePerUsd { get; set; } = 1m;

    public DetailModel(ICoinRepository coins, IFiatRateService fxRates)
    {
        _coins = coins;
        _fxRates = fxRates;
    }

    public async Task<IActionResult> OnGetAsync(string symbol)
    {
        var all = await _coins.GetAllAsync();
        Coin = all.FirstOrDefault(c => c.Symbol.ToUpper() == symbol.ToUpper());
        Monero = all.FirstOrDefault(c => c.Symbol.ToUpper() == "XMR");

        if (Coin == null) return NotFound();

        Currency = CurrencyResolver.Resolve(HttpContext);
        var rates = await _fxRates.GetRatesAsync(HttpContext.RequestAborted);
        RatePerUsd = rates.TryGetValue(Currency.Code, out var r) && r > 0 ? r : 1m;

        // vs XMR comparisons — computed from live CoinGecko percentages so
        // every period uses the same data source as the homepage. No history
        // table lookup, no risk of anchoring to a stale local snapshot.
        if (Monero != null && Coin.Symbol.ToUpper() != "XMR")
        {
            VsXmrChange24h = ComputeVsXmr(Coin.PriceChangePercent24h, Monero.PriceChangePercent24h);
            VsXmrChange7d  = ComputeVsXmr(Coin.PriceChangePercent7d,  Monero.PriceChangePercent7d);
            VsXmrChange30d = ComputeVsXmr(Coin.PriceChangePercent30d, Monero.PriceChangePercent30d);
            VsXmrChange1yr = ComputeVsXmr(Coin.PriceChangePercent1y,  Monero.PriceChangePercent1y);
        }

        return Page();
    }

    /// <summary>
    /// Relative performance of a coin vs XMR over the same window.
    /// (1 + coin%) / (1 + xmr%) - 1, expressed as %.
    /// </summary>
    private static decimal? ComputeVsXmr(decimal coinChange, decimal xmrChange)
    {
        if (xmrChange == -100m) return null;
        return ((1m + coinChange / 100m) / (1m + xmrChange / 100m) - 1m) * 100m;
    }
}