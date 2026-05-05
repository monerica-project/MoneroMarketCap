using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Data.Repositories;

namespace MoneroMarketCap.Pages.Coins;

public class DetailModel : PageModel
{
    private readonly ICoinRepository _coins;
    private readonly AppDbContext _db;

    public Coin? Coin { get; set; }
    public Coin? Monero { get; set; }

    public decimal? PriceChange1yr { get; set; }

    public decimal? VsXmrChange24h { get; set; }
    public decimal? VsXmrChange7d { get; set; }
    public decimal? VsXmrChange30d { get; set; }
    public decimal? VsXmrChange1yr { get; set; }

    public DetailModel(ICoinRepository coins, AppDbContext db)
    {
        _coins = coins;
        _db = db;
    }

    public async Task<IActionResult> OnGetAsync(string symbol)
    {
        var all = await _coins.GetAllAsync();
        Coin = all.FirstOrDefault(c => c.Symbol.ToUpper() == symbol.ToUpper());
        Monero = all.FirstOrDefault(c => c.Symbol.ToUpper() == "XMR");

        if (Coin == null) return NotFound();

        var oneYearAgo  = DateTime.UtcNow.Date.AddDays(-365);
        var thirtyDaysAgo = DateTime.UtcNow.Date.AddDays(-30);
        var sevenDaysAgo  = DateTime.UtcNow.Date.AddDays(-7);

        // 1yr USD price change
        var coinHistory1yr = await _db.CoinPriceHistories
            .Where(h => h.CoinId == Coin.Id && h.Interval == "1d" && h.RecordedAt >= oneYearAgo)
            .OrderBy(h => h.RecordedAt)
            .FirstOrDefaultAsync();

        if (coinHistory1yr != null
            && coinHistory1yr.PriceUsd > 0
            && Coin.PriceUsd > 0)
        {
            PriceChange1yr = ((Coin.PriceUsd - coinHistory1yr.PriceUsd) / coinHistory1yr.PriceUsd) * 100;
        }

        // vs XMR comparisons
        if (Monero != null && Coin.Symbol.ToUpper() != "XMR")
        {
            // 24h vs XMR — computed from live values, no history lookup needed
            if (Monero.PriceChangePercent24h != -100m)
            {
                VsXmrChange24h = ((1m + Coin.PriceChangePercent24h / 100m)
                                / (1m + Monero.PriceChangePercent24h / 100m) - 1m) * 100m;
            }

            var xmrHistory1yr = await _db.CoinPriceHistories
                .Where(h => h.Coin.Symbol == "XMR" && h.Interval == "1d" && h.RecordedAt >= oneYearAgo)
                .OrderBy(h => h.RecordedAt)
                .FirstOrDefaultAsync();

            var xmrHistory30d = await _db.CoinPriceHistories
                .Where(h => h.Coin.Symbol == "XMR" && h.Interval == "1d" && h.RecordedAt >= thirtyDaysAgo)
                .OrderBy(h => h.RecordedAt)
                .FirstOrDefaultAsync();

            var xmrHistory7d = await _db.CoinPriceHistories
                .Where(h => h.Coin.Symbol == "XMR" && h.Interval == "1d" && h.RecordedAt >= sevenDaysAgo)
                .OrderBy(h => h.RecordedAt)
                .FirstOrDefaultAsync();

            var coinHistory30d = await _db.CoinPriceHistories
                .Where(h => h.CoinId == Coin.Id && h.Interval == "1d" && h.RecordedAt >= thirtyDaysAgo)
                .OrderBy(h => h.RecordedAt)
                .FirstOrDefaultAsync();

            var coinHistory7d = await _db.CoinPriceHistories
                .Where(h => h.CoinId == Coin.Id && h.Interval == "1d" && h.RecordedAt >= sevenDaysAgo)
                .OrderBy(h => h.RecordedAt)
                .FirstOrDefaultAsync();

            if (coinHistory1yr != null && xmrHistory1yr != null
                && coinHistory1yr.PriceUsd > 0 && xmrHistory1yr.PriceUsd > 0
                && Monero.PriceUsd > 0 && Coin.PriceUsd > 0)
            {
                var ratioThen = coinHistory1yr.PriceUsd / xmrHistory1yr.PriceUsd;
                var ratioNow  = Coin.PriceUsd / Monero.PriceUsd;
                VsXmrChange1yr = ((ratioNow - ratioThen) / ratioThen) * 100;
            }

            if (coinHistory30d != null && xmrHistory30d != null
                && coinHistory30d.PriceUsd > 0 && xmrHistory30d.PriceUsd > 0
                && Monero.PriceUsd > 0 && Coin.PriceUsd > 0)
            {
                var ratioThen = coinHistory30d.PriceUsd / xmrHistory30d.PriceUsd;
                var ratioNow  = Coin.PriceUsd / Monero.PriceUsd;
                VsXmrChange30d = ((ratioNow - ratioThen) / ratioThen) * 100;
            }

            if (coinHistory7d != null && xmrHistory7d != null
                && coinHistory7d.PriceUsd > 0 && xmrHistory7d.PriceUsd > 0
                && Monero.PriceUsd > 0 && Coin.PriceUsd > 0)
            {
                var ratioThen = coinHistory7d.PriceUsd / xmrHistory7d.PriceUsd;
                var ratioNow  = Coin.PriceUsd / Monero.PriceUsd;
                VsXmrChange7d = ((ratioNow - ratioThen) / ratioThen) * 100;
            }
        }

        return Page();
    }
}