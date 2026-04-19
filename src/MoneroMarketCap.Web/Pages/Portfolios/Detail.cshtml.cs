using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Data.Repositories;
using System.Security.Claims;

namespace MoneroMarketCap.Pages.Portfolios;

[Authorize]
public class DetailModel : PageModel
{
    private readonly IPortfolioRepository _portfolios;
    private readonly ICoinRepository _coins;
    private readonly AppDbContext _db;

    public Portfolio? Portfolio { get; set; }
    public List<SelectListItem> CoinOptions { get; set; } = new();
    public Dictionary<int, decimal> CoinPrices { get; set; } = new();
    public decimal XmrPrice { get; set; }

    public IReadOnlyList<AllocationSlice> Allocations { get; set; } = new List<AllocationSlice>();
    public IReadOnlyList<PnlPoint> PnlSeries { get; set; } = new List<PnlPoint>();

    [BindProperty] public int CoinId { get; set; }
    [BindProperty] public TransactionType TransactionType { get; set; }
    [BindProperty] public decimal Amount { get; set; }
    [BindProperty] public decimal PriceUsdAtTime { get; set; }
    [BindProperty] public string? Notes { get; set; }
    public bool PrivacyMode { get; set; }

    public DetailModel(IPortfolioRepository portfolios, ICoinRepository coins, AppDbContext db)
    {
        _portfolios = portfolios;
        _coins = coins;
        _db = db;
    }

    private int GetUserId() => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private async Task<bool> LoadPortfolio(int id)
    {
        Portfolio = await _portfolios.GetWithDetailsAsync(id);
        return Portfolio != null && Portfolio.UserId == GetUserId();
    }

    private async Task LoadPrivacyModeAsync()
    {
        PrivacyMode = await _db.Users
            .Where(u => u.Id == GetUserId())
            .Select(u => u.PrivacyMode)
            .FirstOrDefaultAsync();
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        if (!await LoadPortfolio(id))
            return NotFound();

        await LoadPrivacyModeAsync();
        await LoadCoinOptions();

        var coins = await _coins.GetAllAsync();
        var xmr = coins.FirstOrDefault(c => c.Symbol.ToUpper() == "XMR");
        if (xmr != null)
        {
            CoinId = xmr.Id;
            PriceUsdAtTime = xmr.PriceUsd;
            XmrPrice = xmr.PriceUsd;
        }

        Allocations = Portfolio!.PortfolioCoins
            .Select(pc => new AllocationSlice
            {
                Symbol = pc.Coin.Symbol.ToUpper(),
                ValueUsd = pc.TotalAmount * pc.Coin.PriceUsd
            })
            .Where(a => a.ValueUsd > 0)
            .OrderByDescending(a => a.ValueUsd)
            .ToList();

        PnlSeries = await BuildPnlSeriesAsync();

        return Page();
    }

    public async Task<IActionResult> OnPostDeletePortfolioAsync(int id)
    {
        if (!await LoadPortfolio(id))
            return NotFound();

        await _portfolios.DeletePortfolioAsync(id);
        return RedirectToPage("/Portfolios/Index");
    }

    public async Task<IActionResult> OnPostAddTransactionAsync(int id)
    {
        if (!await LoadPortfolio(id))
            return NotFound();

        await _portfolios.AddTransactionAsync(id, CoinId, TransactionType, Amount, PriceUsdAtTime, Notes);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteTransactionAsync(int id, int transactionId)
    {
        if (!await LoadPortfolio(id))
            return NotFound();

        var tx = Portfolio!.PortfolioCoins
            .SelectMany(pc => pc.Transactions)
            .FirstOrDefault(t => t.Id == transactionId);

        if (tx == null)
            return NotFound();

        await _portfolios.DeleteTransactionAsync(transactionId);
        return RedirectToPage(new { id });
    }

    private async Task LoadCoinOptions()
    {
        var coins = await _coins.GetAllAsync();
        CoinOptions = coins
            .OrderBy(c => c.Symbol)
            .Select(c => new SelectListItem($"{c.Symbol} - {c.Name}", c.Id.ToString()))
            .ToList();
        CoinPrices = coins.ToDictionary(c => c.Id, c => c.PriceUsd);
    }

    private async Task<IReadOnlyList<PnlPoint>> BuildPnlSeriesAsync()
    {
        if (Portfolio == null || !Portfolio.PortfolioCoins.Any())
            return new List<PnlPoint>();

        var allTxs = Portfolio.PortfolioCoins
            .SelectMany(pc => pc.Transactions.Select(t => new
            {
                CoinId = pc.CoinId,
                Symbol = pc.Coin.Symbol.ToUpper(),
                CoinGeckoId = pc.Coin.CoinGeckoId,
                t.TransactedAt,
                t.Type,
                t.Amount,
                t.PriceUsdAtTime,
                t.TotalUsd
            }))
            .OrderBy(t => t.TransactedAt)
            .ToList();

        if (!allTxs.Any()) return new List<PnlPoint>();

        var startDate = allTxs.First().TransactedAt.Date;
        var endDate = DateTime.UtcNow.Date;

        var coinGeckoIds = Portfolio.PortfolioCoins
            .Select(pc => pc.Coin.CoinGeckoId)
            .Distinct()
            .ToList();

        var historyRaw = await _db.CoinPriceHistories
            .Where(h => coinGeckoIds.Contains(h.Coin.CoinGeckoId)
                     && h.Interval == "1d"
                     && h.RecordedAt >= startDate)
            .Select(h => new
            {
                CoinGeckoId = h.Coin.CoinGeckoId,
                Date = h.RecordedAt.Date,
                h.PriceUsd
            })
            .ToListAsync();

        var priceLookup = historyRaw
            .GroupBy(h => h.CoinGeckoId)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(x => x.Date)
                      .ToDictionary(dg => dg.Key, dg => dg.Last().PriceUsd));

        var xmrGeckoId = Portfolio.PortfolioCoins
            .FirstOrDefault(pc => pc.Coin.Symbol.ToUpper() == "XMR")?.Coin.CoinGeckoId;
        if (xmrGeckoId == null)
        {
            var xmrCoin = await _db.Coins.FirstOrDefaultAsync(c => c.Symbol.ToUpper() == "XMR");
            if (xmrCoin != null)
            {
                xmrGeckoId = xmrCoin.CoinGeckoId;
                var xmrHistory = await _db.CoinPriceHistories
                    .Where(h => h.Coin.CoinGeckoId == xmrGeckoId
                             && h.Interval == "1d"
                             && h.RecordedAt >= startDate)
                    .Select(h => new { Date = h.RecordedAt.Date, h.PriceUsd })
                    .ToListAsync();
                priceLookup[xmrGeckoId] = xmrHistory
                    .GroupBy(x => x.Date)
                    .ToDictionary(dg => dg.Key, dg => dg.Last().PriceUsd);
            }
        }

        var result = new List<PnlPoint>();
        var holdings = new Dictionary<int, decimal>();
        var costBasis = new Dictionary<int, decimal>();
        var coinIdToGeckoId = Portfolio.PortfolioCoins
            .ToDictionary(pc => pc.CoinId, pc => pc.Coin.CoinGeckoId);

        int txIdx = 0;
        var lastPrice = new Dictionary<string, decimal>();

        for (var day = startDate; day <= endDate; day = day.AddDays(1))
        {
            while (txIdx < allTxs.Count && allTxs[txIdx].TransactedAt.Date <= day)
            {
                var tx = allTxs[txIdx];
                if (!holdings.ContainsKey(tx.CoinId))
                {
                    holdings[tx.CoinId] = 0;
                    costBasis[tx.CoinId] = 0;
                }
                if (tx.Type == TransactionType.Buy)
                {
                    holdings[tx.CoinId] += tx.Amount;
                    costBasis[tx.CoinId] += tx.TotalUsd;
                }
                else
                {
                    var prevAmount = holdings[tx.CoinId];
                    if (prevAmount > 0)
                    {
                        var proportion = Math.Min(tx.Amount / prevAmount, 1m);
                        costBasis[tx.CoinId] -= costBasis[tx.CoinId] * proportion;
                    }
                    holdings[tx.CoinId] -= tx.Amount;
                    if (holdings[tx.CoinId] < 0) holdings[tx.CoinId] = 0;
                }
                txIdx++;
            }

            decimal valueUsd = 0;
            decimal totalBasis = 0;
            foreach (var kv in holdings)
            {
                if (kv.Value <= 0) continue;
                var gecko = coinIdToGeckoId[kv.Key];
                decimal price = 0;
                if (priceLookup.TryGetValue(gecko, out var coinPrices)
                    && coinPrices.TryGetValue(day, out var p))
                {
                    price = p;
                    lastPrice[gecko] = p;
                }
                else if (lastPrice.TryGetValue(gecko, out var lp))
                {
                    price = lp;
                }
                valueUsd += kv.Value * price;
                totalBasis += costBasis[kv.Key];
            }

            var pnlUsd = valueUsd - totalBasis;

            decimal xmrPriceThisDay = 0;
            if (xmrGeckoId != null && priceLookup.TryGetValue(xmrGeckoId, out var xmrPrices))
            {
                if (xmrPrices.TryGetValue(day, out var xp))
                {
                    xmrPriceThisDay = xp;
                    lastPrice[xmrGeckoId] = xp;
                }
                else if (lastPrice.TryGetValue(xmrGeckoId, out var lastXmr))
                {
                    xmrPriceThisDay = lastXmr;
                }
            }

            var pnlXmr = xmrPriceThisDay > 0 ? pnlUsd / xmrPriceThisDay : 0;

            result.Add(new PnlPoint
            {
                TimestampMs = new DateTimeOffset(day, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                PnlUsd = (double)pnlUsd,
                PnlXmr = (double)pnlXmr
            });
        }

        return result;
    }

    public class AllocationSlice
    {
        public string Symbol { get; set; } = "";
        public decimal ValueUsd { get; set; }
    }

    public class PnlPoint
    {
        public long TimestampMs { get; set; }
        public double PnlUsd { get; set; }
        public double PnlXmr { get; set; }
    }
}