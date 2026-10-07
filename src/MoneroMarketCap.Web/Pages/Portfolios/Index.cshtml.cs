using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Data.Repositories;
using MoneroMarketCap.Services.Interfaces;
using MoneroMarketCap.Services.Models;
using MoneroMarketCap.Web.Helpers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace MoneroMarketCap.Pages.Portfolios;

[Authorize]
public class IndexModel : PageModel
{
    public const int MaxPortfoliosPerUser = 6;

    private readonly IPortfolioRepository _portfolios;
    private readonly ICoinRepository _coins;
    private readonly AppDbContext _db;
    private readonly IFiatRateService _fxRates;

    public IReadOnlyList<Portfolio> Portfolios { get; set; } = new List<Portfolio>();
    public decimal TotalNetValue { get; set; }
    public decimal TotalCostBasis { get; set; }
    public decimal TotalPnl { get; set; }
    public decimal XmrPrice { get; set; }
    public bool PrivacyMode { get; set; }
    public bool AtPortfolioLimit => Portfolios.Count >= MaxPortfoliosPerUser;
    public string DataVersion { get; set; } = "";

    // Total-portfolio value over the last year (or since the portfolio started, if younger),
    // one point per day in USD and XMR, as JSON: [{"d":"yyyy-MM-dd","u":<usd>,"x":<xmr>}].
    // Rendered as a line chart with a USD/XMR toggle.
    public string ValueHistoryJson { get; set; } = "[]";
    public bool HasValueHistory { get; set; }

    public CurrencyInfo Currency { get; set; } = CurrencyCatalog.Default;
    public decimal RatePerUsd { get; set; } = 1m;

    public IReadOnlyList<AllocationSlice> Allocations { get; set; } = new List<AllocationSlice>();

    [BindProperty] public string PortfolioName { get; set; } = "My Portfolio";

    [TempData] public string? FlashMessage { get; set; }
    [TempData] public bool FlashSuccess { get; set; }

    public IndexModel(
        IPortfolioRepository portfolios,
        ICoinRepository coins,
        AppDbContext db,
        IFiatRateService fxRates)
    {
        _portfolios = portfolios;
        _coins = coins;
        _db = db;
        _fxRates = fxRates;
    }

    private int GetUserId() => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public async Task OnGetAsync()
    {
        await ResolveCurrencyAsync();

        var snapshot = await BuildSnapshotAsync(GetUserId());

        PrivacyMode = snapshot.PrivacyMode;
        Portfolios = snapshot.Portfolios;
        TotalNetValue = snapshot.TotalNetValue;
        XmrPrice = snapshot.XmrPrice;
        Allocations = snapshot.Allocations;
        TotalCostBasis = snapshot.TotalCostBasis;
        TotalPnl = snapshot.TotalPnl;
        DataVersion = snapshot.Version;

        await BuildValueHistoryAsync(GetUserId());
    }

    // Reconstructs the daily total value of ALL the user's holdings over the window
    // [max(firstTxDate, 1 year ago) .. today]. Holdings on each day come from the running
    // buy/sell balance; prices come from CoinPriceHistory ("1d", forward-filled); XMR value
    // divides the USD total by XMR's price that day. Cheap: O(days * coins), window <= ~366 days.
    private async Task BuildValueHistoryAsync(int userId, CancellationToken ct = default)
    {
        var txs = await _db.CoinTransactions
            .Where(t => t.PortfolioCoin.Portfolio.UserId == userId)
            .OrderBy(t => t.TransactedAt)
            .Select(t => new { t.PortfolioCoin.CoinId, t.Type, t.Amount, t.TransactedAt })
            .ToListAsync(ct);
        if (txs.Count == 0)
        {
            ValueHistoryJson = "[]";
            HasValueHistory = false;
            return;
        }

        var today = DateTime.UtcNow.Date;
        var firstDate = txs[0].TransactedAt.Date;
        var startDate = firstDate > today.AddDays(-365) ? firstDate : today.AddDays(-365);

        var coinIds = txs.Select(t => t.CoinId).Distinct().ToList();
        var xmrId = await _db.Coins.Where(c => c.Symbol.ToUpper() == "XMR").Select(c => (int?)c.Id).FirstOrDefaultAsync(ct);
        var priceIds = coinIds.ToList();
        if (xmrId is int xid && !priceIds.Contains(xid)) priceIds.Add(xid);

        var histFrom = startDate.AddDays(-35);
        var rawHist = await _db.CoinPriceHistories
            .Where(h => priceIds.Contains(h.CoinId) && h.Interval == "1d" && h.RecordedAt >= histFrom)
            .Select(h => new { h.CoinId, h.RecordedAt, h.PriceUsd })
            .ToListAsync(ct);
        var priceByCoin = rawHist
            .GroupBy(h => h.CoinId)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(h => h.RecordedAt.Date)
                      .Select(dg => (Date: dg.Key, Price: dg.OrderBy(h => h.RecordedAt).Last().PriceUsd))
                      .OrderBy(x => x.Date)
                      .ToList());
        var currentPrice = await _db.Coins
            .Where(c => priceIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.PriceUsd, ct);

        // Opening balance from every transaction BEFORE the window start.
        var balance = coinIds.ToDictionary(id => id, _ => 0m);
        int ti = 0;
        for (; ti < txs.Count && txs[ti].TransactedAt.Date < startDate; ti++)
        {
            var t = txs[ti];
            balance[t.CoinId] += t.Type == TransactionType.Buy ? t.Amount : -t.Amount;
        }

        // Seed each coin's forward-fill price to the latest row on/before startDate.
        var lastPrice = new Dictionary<int, decimal>();
        var pIdx = new Dictionary<int, int>();
        foreach (var id in priceIds)
        {
            if (priceByCoin.TryGetValue(id, out var list) && list.Count > 0)
            {
                int i = 0;
                decimal lp = list[0].Price;
                while (i < list.Count && list[i].Date <= startDate) { lp = list[i].Price; i++; }
                lastPrice[id] = lp;
                pIdx[id] = i;
            }
            else
            {
                lastPrice[id] = currentPrice.GetValueOrDefault(id, 0m);
                pIdx[id] = 0;
            }
        }

        var points = new List<object>();
        for (var day = startDate; day <= today; day = day.AddDays(1))
        {
            while (ti < txs.Count && txs[ti].TransactedAt.Date <= day)
            {
                var t = txs[ti];
                balance[t.CoinId] += t.Type == TransactionType.Buy ? t.Amount : -t.Amount;
                ti++;
            }
            foreach (var id in priceIds)
            {
                if (priceByCoin.TryGetValue(id, out var list))
                {
                    int i = pIdx[id];
                    while (i < list.Count && list[i].Date <= day) { lastPrice[id] = list[i].Price; i++; }
                    pIdx[id] = i;
                }
            }
            decimal usd = 0m;
            foreach (var id in coinIds)
            {
                var bal = balance[id];
                if (bal != 0m) usd += bal * lastPrice.GetValueOrDefault(id, 0m);
            }
            var xmrP = xmrId is int x ? lastPrice.GetValueOrDefault(x, 0m) : 0m;
            var xmr = xmrP > 0 ? usd / xmrP : 0m;
            points.Add(new { d = day.ToString("yyyy-MM-dd"), u = Math.Round(usd, 2), x = Math.Round(xmr, 6) });
        }

        ValueHistoryJson = System.Text.Json.JsonSerializer.Serialize(points);
        HasValueHistory = points.Count > 1;
    }

    public async Task<IActionResult> OnGetSnapshotAsync()
    {
        await ResolveCurrencyAsync();
        var snapshot = await BuildSnapshotAsync(GetUserId());

        return new JsonResult(new
        {
            version = snapshot.Version,
            privacyMode = snapshot.PrivacyMode,
            xmrPrice = snapshot.XmrPrice,
            // All *Usd values stay USD-canonical; client multiplies by ratePerUsd for display.
            totalNetValue = snapshot.TotalNetValue,
            totalPnl = snapshot.TotalPnl,
            totalCostBasis = snapshot.TotalCostBasis,
            currency = new
            {
                code = Currency.Code,
                symbol = Currency.Symbol,
                before = Currency.SymbolBefore,
                decimals = Currency.Decimals,
                ratePerUsd = RatePerUsd,
            },
            allocations = snapshot.Allocations.Select(a => new
            {
                symbol = a.Symbol,
                priceUsd = a.PriceUsd,
                totalAmount = a.TotalAmount,
                valueUsd = a.ValueUsd
            }),
            portfolios = snapshot.Portfolios.Select(p => new
            {
                id = p.Id,
                totalValueUsd = p.TotalValueUsd,
                pnl = p.PortfolioCoins.Sum(pc => pc.UnrealizedPnl),
                costBasis = p.PortfolioCoins.Sum(pc => pc.TotalCostBasis),
                coinCount = p.PortfolioCoins.Count
            })
        });
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        var userId = GetUserId();

        var existingCount = await _db.Portfolios.CountAsync(p => p.UserId == userId);
        if (existingCount >= MaxPortfoliosPerUser)
        {
            FlashMessage = $"You've reached the limit of {MaxPortfoliosPerUser} portfolios. Delete an existing one to create a new portfolio.";
            FlashSuccess = false;
            return RedirectToPage();
        }

        await _portfolios.AddAsync(new Portfolio
        {
            UserId = userId,
            Name = string.IsNullOrWhiteSpace(PortfolioName) ? "My Portfolio" : PortfolioName
        });
        await _portfolios.SaveChangesAsync();

        return RedirectToPage();
    }

    private async Task ResolveCurrencyAsync()
    {
        Currency = CurrencyResolver.Resolve(HttpContext);
        var rates = await _fxRates.GetRatesAsync(HttpContext.RequestAborted);
        RatePerUsd = rates.TryGetValue(Currency.Code, out var r) && r > 0 ? r : 1m;
    }

    private async Task<SnapshotData> BuildSnapshotAsync(int userId)
    {
        var privacyMode = await _db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.PrivacyMode)
            .FirstOrDefaultAsync();

        var portfolios = await _portfolios.GetByUserIdAsync(userId);
        var totalNetValue = await _portfolios.GetUserTotalValueUsdAsync(userId);

        var allCoins = await _coins.GetAllAsync();
        var xmr = allCoins.FirstOrDefault(c => c.Symbol.ToUpper() == "XMR");
        var xmrPrice = xmr?.PriceUsd ?? 0;

        var allocations = portfolios
            .SelectMany(p => p.PortfolioCoins)
            .GroupBy(pc => pc.Coin.Symbol.ToUpper())
            .Select(g => new AllocationSlice
            {
                Symbol = g.Key,
                PriceUsd = g.First().Coin.PriceUsd,
                TotalAmount = g.Sum(pc => pc.TotalAmount),
                ValueUsd = g.Sum(pc => pc.TotalAmount * pc.Coin.PriceUsd)
            })
            .Where(a => a.ValueUsd > 0)
            .OrderByDescending(a => a.ValueUsd)
            .ToList();

        var totalCostBasis = portfolios
            .SelectMany(p => p.PortfolioCoins)
            .Sum(pc => pc.TotalCostBasis);

        var totalPnl = portfolios
            .SelectMany(p => p.PortfolioCoins)
            .Sum(pc => pc.UnrealizedPnl);

        var version = ComputeVersion(
            portfolios,
            totalNetValue,
            totalPnl,
            allocations.Select(a => (a.Symbol, a.PriceUsd, a.TotalAmount)));

        return new SnapshotData
        {
            PrivacyMode = privacyMode,
            Portfolios = portfolios,
            TotalNetValue = totalNetValue,
            XmrPrice = xmrPrice,
            Allocations = allocations,
            TotalCostBasis = totalCostBasis,
            TotalPnl = totalPnl,
            Version = version
        };
    }

    private static string ComputeVersion(
        IEnumerable<Portfolio> portfolios,
        decimal totalNetValue,
        decimal totalPnl,
        IEnumerable<(string Symbol, decimal PriceUsd, decimal TotalAmount)> allocations)
    {
        var sb = new StringBuilder();
        sb.Append(totalNetValue.ToString("F8")).Append('|');
        sb.Append(totalPnl.ToString("F8"));

        foreach (var a in allocations.OrderBy(x => x.Symbol, StringComparer.Ordinal))
        {
            sb.Append('|').Append(a.Symbol).Append(':')
              .Append(a.PriceUsd.ToString("F8")).Append(':')
              .Append(a.TotalAmount.ToString("F8"));
        }
        foreach (var p in portfolios.OrderBy(x => x.Id))
        {
            sb.Append('|').Append(p.Id).Append(':')
              .Append(p.TotalValueUsd.ToString("F8"));
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }

    private class SnapshotData
    {
        public bool PrivacyMode { get; set; }
        public IReadOnlyList<Portfolio> Portfolios { get; set; } = new List<Portfolio>();
        public decimal TotalNetValue { get; set; }
        public decimal XmrPrice { get; set; }
        public IReadOnlyList<AllocationSlice> Allocations { get; set; } = new List<AllocationSlice>();
        public decimal TotalCostBasis { get; set; }
        public decimal TotalPnl { get; set; }
        public string Version { get; set; } = "";
    }

    public class AllocationSlice
    {
        public string Symbol { get; set; } = "";
        public decimal PriceUsd { get; set; }
        public decimal TotalAmount { get; set; }
        public decimal ValueUsd { get; set; }
    }
}
