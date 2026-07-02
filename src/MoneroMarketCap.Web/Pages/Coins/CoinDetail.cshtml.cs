using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data;
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
    private readonly IFiatRateHistoryService _fxHistory;
    private readonly IChangeNowLinkService _changeNow;
    private readonly AppDbContext _db;

    public Coin? Coin { get; set; }
    public Coin? Monero { get; set; }

    // Exchanges (from SwapRaven) that support this coin, graded best-first.
    // Paged: only the current page's slice is loaded; totals drive the pager.
    public List<CoinExchange> Exchanges { get; set; } = new();
    public const int ExPageSize = 25;
    public int ExPage { get; set; } = 1;
    public int ExTotal { get; set; }
    public int ExTotalPages => (int)Math.Ceiling(ExTotal / (double)ExPageSize);

    // Affiliate link actually rendered by the view. Admin TradeUrl wins; otherwise
    // built from the coin's resolved ChangeNOW ticker + the config template.
    public string? EffectiveTradeUrl { get; set; }

    // Currency-adjusted percentage returns for the selected display currency.
    // Default to the raw USD values from CoinGecko; recomputed against
    // FiatRateHistory when a non-USD currency is active and historical FX
    // for that currency/date is available.
    public decimal PriceChange1h  { get; set; }
    public decimal PriceChange24h { get; set; }
    public decimal PriceChange7d  { get; set; }
    public decimal PriceChange30d { get; set; }
    public decimal PriceChange1y  { get; set; }

    public decimal? VsXmrChange24h { get; set; }
    public decimal? VsXmrChange7d { get; set; }
    public decimal? VsXmrChange30d { get; set; }
    public decimal? VsXmrChange1yr { get; set; }

    public CurrencyInfo Currency { get; set; } = CurrencyCatalog.Default;
    public decimal RatePerUsd { get; set; } = 1m;

    public DetailModel(
        ICoinRepository coins,
        IFiatRateService fxRates,
        IFiatRateHistoryService fxHistory,
        IChangeNowLinkService changeNow,
        AppDbContext db)
    {
        _coins = coins;
        _fxRates = fxRates;
        _fxHistory = fxHistory;
        _changeNow = changeNow;
        _db = db;
    }

    /// <summary>Turns an enum-name like "LikelyIfSuspicious" into "Likely if suspicious".</summary>
    public static string Humanize(string? enumName)
    {
        if (string.IsNullOrWhiteSpace(enumName))
        {
            return "—";
        }

        var spaced = System.Text.RegularExpressions.Regex.Replace(enumName, "(?<=[a-z0-9])(?=[A-Z])", " ");
        return spaced.Length > 1 ? char.ToUpperInvariant(spaced[0]) + spaced.Substring(1).ToLowerInvariant() : spaced;
    }

    /// <summary>Compact fee display, e.g. "0.4%–0.8%", "1.5%", "Varies", or "—".</summary>
    public static string FormatFees(CoinExchange e)
    {
        if (e.FeeVariesByProvider)
        {
            return "Varies";
        }

        if (e.FeeMinPercent.HasValue && e.FeeMaxPercent.HasValue)
        {
            return e.FeeMinPercent.Value == e.FeeMaxPercent.Value
                ? $"{e.FeeMinPercent.Value:0.##}%"
                : $"{e.FeeMinPercent.Value:0.##}%–{e.FeeMaxPercent.Value:0.##}%";
        }

        if (e.FeeMinPercent.HasValue)
        {
            return $"{e.FeeMinPercent.Value:0.##}%";
        }

        return "—";
    }

    public async Task<IActionResult> OnGetAsync(string symbol, int xp = 1)
    {
        var all = await _coins.GetAllAsync();
        Coin = all.FirstOrDefault(c => c.Symbol.ToUpper() == symbol.ToUpper());
        Monero = all.FirstOrDefault(c => c.Symbol.ToUpper() == "XMR");

        if (Coin == null) return NotFound();

        // Exchanges that support this coin (synced weekly from SwapRaven), graded
        // best-first, 25 per page.
        ExTotal = await _db.CoinExchanges.CountAsync(e => e.CoinId == Coin.Id, HttpContext.RequestAborted);
        ExPage = Math.Clamp(xp, 1, Math.Max(1, ExTotalPages));
        Exchanges = await _db.CoinExchanges.AsNoTracking()
            .Where(e => e.CoinId == Coin.Id)
            .OrderBy(e => e.SortOrder)
            .ThenBy(e => e.Name)
            .Skip((ExPage - 1) * ExPageSize)
            .Take(ExPageSize)
            .ToListAsync(HttpContext.RequestAborted);

        Currency = CurrencyResolver.Resolve(HttpContext);
        var rates = await _fxRates.GetRatesAsync(HttpContext.RequestAborted);
        RatePerUsd = rates.TryGetValue(Currency.Code, out var r) && r > 0 ? r : 1m;

        // Start with raw USD values from CoinGecko. These get overwritten below
        // when the selected currency is non-USD and FX history is available.
        // If anything fails to look up, the USD value just stays as the answer
        // — silent graceful degradation, same as the chart's fx=1 fallback.
        PriceChange1h  = Coin.PriceChangePercent1h;
        PriceChange24h = Coin.PriceChangePercent24h;
        PriceChange7d  = Coin.PriceChangePercent7d;
        PriceChange30d = Coin.PriceChangePercent30d;
        PriceChange1y  = Coin.PriceChangePercent1y;

        // Restate USD percentage returns in the selected currency:
        //   pct_curr = ((1 + pct_usd/100) * (fx_now / fx_then) - 1) * 100
        //
        // FiatRateHistory is daily-granularity, so the 1h window is left as the
        // USD value (fx_now ≈ fx_1h_ago at sub-daily resolution).
        if (Currency.Code != "USD" && RatePerUsd > 0)
        {
            var today = DateTime.UtcNow.Date;
            var fxSeries = await _fxHistory.GetSeriesAsync(
                Currency.Code,
                today.AddDays(-380),   // 1y window + buffer for backward walk
                today);

            // Walk backward up to 14 days from `anchor` to find an FX rate
            // (covers weekends, ECB holidays, occasional source gaps).
            decimal? FxOn(DateTime anchor)
            {
                for (var i = 0; i < 14; i++)
                {
                    if (fxSeries.TryGetValue(anchor.AddDays(-i), out var rate) && rate > 0)
                        return rate;
                }
                return null;
            }

            decimal Restate(decimal usdPct, DateTime anchorDate)
            {
                var fxThen = FxOn(anchorDate);
                if (!fxThen.HasValue || fxThen.Value <= 0) return usdPct;
                return ((1m + usdPct / 100m) * (RatePerUsd / fxThen.Value) - 1m) * 100m;
            }

            PriceChange24h = Restate(Coin.PriceChangePercent24h, today.AddDays(-1));
            PriceChange7d  = Restate(Coin.PriceChangePercent7d,  today.AddDays(-7));
            PriceChange30d = Restate(Coin.PriceChangePercent30d, today.AddDays(-30));
            PriceChange1y  = Restate(Coin.PriceChangePercent1y,  today.AddDays(-365));
        }

        // vs XMR comparisons — crypto-to-crypto ratios where the FX rate cancels
        // out on both sides, so no currency adjustment is needed. Use live
        // CoinGecko percentages directly.
        if (Monero != null && Coin.Symbol.ToUpper() != "XMR")
        {
            VsXmrChange24h = ComputeVsXmr(Coin.PriceChangePercent24h, Monero.PriceChangePercent24h);
            VsXmrChange7d  = ComputeVsXmr(Coin.PriceChangePercent7d,  Monero.PriceChangePercent7d);
            VsXmrChange30d = ComputeVsXmr(Coin.PriceChangePercent30d, Monero.PriceChangePercent30d);
            VsXmrChange1yr = ComputeVsXmr(Coin.PriceChangePercent1y,  Monero.PriceChangePercent1y);
        }

         // ChangeNOW affiliate link — READ ONLY. Admin TradeUrl wins; otherwise build the
        // link from the ticker the Worker already resolved. The Web never resolves or
        // writes here (no network on the request path).
        var isXmr = Coin.Symbol.ToUpper() == "XMR";
        if (!isXmr)
        {
            EffectiveTradeUrl = !string.IsNullOrEmpty(Coin.TradeUrl)
                ? Coin.TradeUrl
                : (!string.IsNullOrEmpty(Coin.ChangeNowTicker)
                    ? _changeNow.BuildTradeUrl(Coin.ChangeNowTicker)
                    : null);
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