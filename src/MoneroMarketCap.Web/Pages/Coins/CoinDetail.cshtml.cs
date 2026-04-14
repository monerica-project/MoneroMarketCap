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

    // % change in USD price over the past year
    public decimal? PriceChange1yr { get; set; }

    // % change of coin/XMR ratio over the past year
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

        var oneYearAgo = DateTime.UtcNow.Date.AddDays(-365);

        // 1yr USD price change
        var coinHistory = await _db.CoinPriceHistories
            .Where(h => h.CoinId == Coin.Id && h.Interval == "1d" && h.RecordedAt >= oneYearAgo)
            .OrderBy(h => h.RecordedAt)
            .FirstOrDefaultAsync();

        if (coinHistory != null && coinHistory.PriceUsd > 0 && Coin.PriceUsd > 0)
        {
            PriceChange1yr = ((Coin.PriceUsd - coinHistory.PriceUsd) / coinHistory.PriceUsd) * 100;
        }

        // vs XMR 1yr ratio change
        if (Monero != null && Coin.Symbol.ToUpper() != "XMR")
        {
            var xmrHistory = await _db.CoinPriceHistories
                .Where(h => h.Coin.Symbol == "XMR" && h.Interval == "1d" && h.RecordedAt >= oneYearAgo)
                .OrderBy(h => h.RecordedAt)
                .FirstOrDefaultAsync();

            if (coinHistory != null && xmrHistory != null
                && coinHistory.PriceUsd > 0 && xmrHistory.PriceUsd > 0
                && Monero.PriceUsd > 0 && Coin.PriceUsd > 0)
            {
                var ratioThen = coinHistory.PriceUsd / xmrHistory.PriceUsd;
                var ratioNow = Coin.PriceUsd / Monero.PriceUsd;
                VsXmrChange1yr = ((ratioNow - ratioThen) / ratioThen) * 100;
            }
        }

        return Page();
    }
}
