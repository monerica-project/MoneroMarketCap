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
