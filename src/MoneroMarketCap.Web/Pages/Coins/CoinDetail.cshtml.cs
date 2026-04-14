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

    // How much the coin has changed vs XMR over the past year
    // Positive = coin outperformed XMR, Negative = coin underperformed
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

        // Calculate vs XMR performance over 1 year
        if (Monero != null && Coin.Symbol.ToUpper() != "XMR")
        {
            var oneYearAgo = DateTime.UtcNow.Date.AddDays(-365);

            var coinHistory = await _db.CoinPriceHistories
                .Where(h => h.CoinId == Coin.Id && h.Interval == "1d" && h.RecordedAt >= oneYearAgo)
                .OrderBy(h => h.RecordedAt)
                .FirstOrDefaultAsync();

            var xmrHistory = await _db.CoinPriceHistories
                .Where(h => h.Coin.Symbol == "XMR" && h.Interval == "1d" && h.RecordedAt >= oneYearAgo)
                .OrderBy(h => h.RecordedAt)
                .FirstOrDefaultAsync();

            if (coinHistory != null && xmrHistory != null
                && coinHistory.PriceUsd > 0 && xmrHistory.PriceUsd > 0
                && Monero.PriceUsd > 0 && Coin.PriceUsd > 0)
            {
                // Ratio then vs ratio now
                var ratioThen = coinHistory.PriceUsd / xmrHistory.PriceUsd;
                var ratioNow = Coin.PriceUsd / Monero.PriceUsd;
                VsXmrChange1yr = ((ratioNow - ratioThen) / ratioThen) * 100;
            }
        }

        return Page();
    }
}
