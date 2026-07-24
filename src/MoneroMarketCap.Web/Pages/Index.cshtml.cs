using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Data.Repositories;
using MoneroMarketCap.Services.Display;
using MoneroMarketCap.Services.Interfaces;
using MoneroMarketCap.Services.Models;
using MoneroMarketCap.Web.Helpers;

namespace MoneroMarketCap.Pages;

public class IndexModel : PageModel
{
    private readonly ICoinRepository _coins;
    private readonly IFiatRateService _fxRates;
    private readonly IConfiguration _config;

    /// <summary>Coins shown per page of the market table.</summary>
    public const int CoinsPerPage = 100;

    public int SponsorRotateIntervalSeconds { get; set; }

    /// <summary>All active coins (the CoinGecko top N). Used for the summary widgets.</summary>
    public IReadOnlyList<Coin> Coins { get; set; } = new List<Coin>();

    /// <summary>Just the slice of <see cref="Coins"/> rendered in the table.</summary>
    public IReadOnlyList<Coin> PagedCoins { get; set; } = new List<Coin>();

    /// <summary>1-based page number, from ?p= (named "p": Razor Pages reserves "page").</summary>
    public int PageNumber { get; set; } = 1;

    public int TotalPages { get; set; } = 1;

    /// <summary>Rank of the first row on this page minus one, so page 2 starts at 101.</summary>
    public int RankOffset => (PageNumber - 1) * CoinsPerPage;

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

    public async Task OnGetAsync(int p = 1)
    {
        Coins = await _coins.GetAllAsync();
        Monero = await _coins.GetByCoinGeckoIdAsync("monero");

        // Order once here, the same way the table used to order inline, then slice —
        // otherwise page 2 would not follow on from page 1.
        var ordered = Coins
            .OrderByDescending(c => MoneroSupplyDisplay.EffectiveMarketCapUsd(c))
            .ToList();

        TotalPages = Math.Max(1, (int)Math.Ceiling(ordered.Count / (double)CoinsPerPage));
        PageNumber = Math.Clamp(p, 1, TotalPages);
        PagedCoins = ordered
            .Skip((PageNumber - 1) * CoinsPerPage)
            .Take(CoinsPerPage)
            .ToList();
        SponsorRotateIntervalSeconds = _config.GetValue<int>("Sponsors:RotateIntervalSeconds", 30);

        Currency = CurrencyResolver.Resolve(HttpContext);
        var rates = await _fxRates.GetRatesAsync(HttpContext.RequestAborted);
        RatePerUsd = rates.TryGetValue(Currency.Code, out var r) && r > 0 ? r : 1m;
    }
}
