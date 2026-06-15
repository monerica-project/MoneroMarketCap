using Microsoft.AspNetCore.Mvc.RazorPages;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Data.Repositories;
using MoneroMarketCap.Services.Display;
using MoneroMarketCap.Services.Interfaces;
using MoneroMarketCap.Services.Models;
using MoneroMarketCap.Web.Helpers;

namespace MoneroMarketCap.Pages;

/// <summary>
/// "vs Monero" leaderboard. Ranks every tracked coin by how it has performed
/// against XMR over several time windows (1h / 24h / 7d / 30d / 1y), so a reader
/// can spot assets that are gaining ground on Monero (candidates to rotate into)
/// or losing to it. All vs-XMR figures are crypto-to-crypto ratios, so they are
/// currency-invariant — only the market-cap column is currency-adjusted.
/// </summary>
public class VsMoneroModel : PageModel
{
    private readonly ICoinRepository _coins;
    private readonly IFiatRateService _fxRates;

    public Coin? Monero { get; set; }
    public IReadOnlyList<VsRow> Rows { get; set; } = new List<VsRow>();

    public CurrencyInfo Currency { get; set; } = CurrencyCatalog.Default;
    public decimal RatePerUsd { get; set; } = 1m;
    public DateTime? LastUpdated { get; set; }

    public VsMoneroModel(ICoinRepository coins, IFiatRateService fxRates)
    {
        _coins = coins;
        _fxRates = fxRates;
    }

    public async Task OnGetAsync()
    {
        var coins = await _coins.GetAllAsync();
        Monero = await _coins.GetByCoinGeckoIdAsync("monero");

        Currency = CurrencyResolver.Resolve(HttpContext);
        var rates = await _fxRates.GetRatesAsync(HttpContext.RequestAborted);
        RatePerUsd = rates.TryGetValue(Currency.Code, out var r) && r > 0 ? r : 1m;

        if (coins.Any())
        {
            LastUpdated = coins.Max(c => c.UpdatedAt);
        }

        var xmr = Monero;

        Rows = coins
            .Where(c => !string.Equals(c.Symbol, "XMR", StringComparison.OrdinalIgnoreCase))
            .Select(c => new VsRow
            {
                Coin = c,
                PriceInXmr = xmr != null && xmr.PriceUsd > 0 ? c.PriceUsd / xmr.PriceUsd : 0m,
                MarketCapUsd = MoneroSupplyDisplay.EffectiveMarketCapUsd(c),
                Vs1h = ComputeVsXmr(c.PriceChangePercent1h, xmr?.PriceChangePercent1h),
                Vs24h = ComputeVsXmr(c.PriceChangePercent24h, xmr?.PriceChangePercent24h),
                Vs7d = ComputeVsXmr(c.PriceChangePercent7d, xmr?.PriceChangePercent7d),
                Vs30d = ComputeVsXmr(c.PriceChangePercent30d, xmr?.PriceChangePercent30d),
                Vs1y = ComputeVsXmr(c.PriceChangePercent1y, xmr?.PriceChangePercent1y),
            })
            // Default ordering: best 7d performers vs XMR first. The view re-sorts
            // client-side when the reader picks a different window.
            .OrderByDescending(x => x.Vs7d ?? decimal.MinValue)
            .ToList();
    }

    /// <summary>
    /// Relative performance of a coin vs XMR over the same window:
    /// (1 + coin%) / (1 + xmr%) - 1, expressed as a percentage. A positive value
    /// means the coin gained ground on Monero; negative means it lost ground.
    /// Returns null when the comparison is undefined (no XMR data, or XMR -100%).
    /// </summary>
    private static decimal? ComputeVsXmr(decimal coinChange, decimal? xmrChange)
    {
        if (xmrChange is null || xmrChange == -100m)
        {
            return null;
        }

        return ((1m + coinChange / 100m) / (1m + xmrChange.Value / 100m) - 1m) * 100m;
    }

    public class VsRow
    {
        public Coin Coin { get; init; } = null!;
        public decimal PriceInXmr { get; init; }
        public decimal MarketCapUsd { get; init; }
        public decimal? Vs1h { get; init; }
        public decimal? Vs24h { get; init; }
        public decimal? Vs7d { get; init; }
        public decimal? Vs30d { get; init; }
        public decimal? Vs1y { get; init; }
    }
}
