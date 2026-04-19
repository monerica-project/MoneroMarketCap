using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Data.Repositories;
using System.Security.Claims;

namespace MoneroMarketCap.Pages.Portfolios;

[Authorize]
public class IndexModel : PageModel
{
    private readonly IPortfolioRepository _portfolios;
    private readonly ICoinRepository _coins;
    private readonly AppDbContext _db;

    public IReadOnlyList<Portfolio> Portfolios { get; set; } = new List<Portfolio>();
    public decimal TotalNetValue { get; set; }
    public decimal TotalCostBasis { get; set; }
    public decimal TotalPnl { get; set; }
    public decimal XmrPrice { get; set; }
    public bool PrivacyMode { get; set; }

    public IReadOnlyList<AllocationSlice> Allocations { get; set; } = new List<AllocationSlice>();

    [BindProperty] public string PortfolioName { get; set; } = "My Portfolio";

    public IndexModel(IPortfolioRepository portfolios, ICoinRepository coins, AppDbContext db)
    {
        _portfolios = portfolios;
        _coins = coins;
        _db = db;
    }

    private int GetUserId() => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public async Task OnGetAsync()
    {
        var userId = GetUserId();

        PrivacyMode = await _db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.PrivacyMode)
            .FirstOrDefaultAsync();

        Portfolios = await _portfolios.GetByUserIdAsync(userId);
        TotalNetValue = await _portfolios.GetUserTotalValueUsdAsync(userId);

        var allCoins = await _coins.GetAllAsync();
        var xmr = allCoins.FirstOrDefault(c => c.Symbol.ToUpper() == "XMR");
        XmrPrice = xmr?.PriceUsd ?? 0;

        Allocations = Portfolios
            .SelectMany(p => p.PortfolioCoins)
            .GroupBy(pc => pc.Coin.Symbol.ToUpper())
            .Select(g => new AllocationSlice
            {
                Symbol = g.Key,
                ValueUsd = g.Sum(pc => pc.TotalAmount * pc.Coin.PriceUsd)
            })
            .Where(a => a.ValueUsd > 0)
            .OrderByDescending(a => a.ValueUsd)
            .ToList();

        TotalCostBasis = Portfolios
            .SelectMany(p => p.PortfolioCoins)
            .Sum(pc => pc.TotalCostBasis);

        TotalPnl = Portfolios
            .SelectMany(p => p.PortfolioCoins)
            .Sum(pc => pc.UnrealizedPnl);
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        var userId = GetUserId();
        await _portfolios.AddAsync(new Portfolio
        {
            UserId = userId,
            Name = string.IsNullOrWhiteSpace(PortfolioName) ? "My Portfolio" : PortfolioName
        });
        await _portfolios.SaveChangesAsync();
        return RedirectToPage();
    }

    public class AllocationSlice
    {
        public string Symbol { get; set; } = "";
        public decimal ValueUsd { get; set; }
    }
}