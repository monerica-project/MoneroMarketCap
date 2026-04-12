using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Data.Repositories;
using System.Security.Claims;

namespace MoneroMarketCap.Pages.Portfolios;

[Authorize]
public class IndexModel : PageModel
{
    private readonly IPortfolioRepository _portfolios;
    private readonly ICoinRepository _coins;

    public IReadOnlyList<Portfolio> Portfolios { get; set; } = new List<Portfolio>();
    public decimal TotalNetValue { get; set; }
    public decimal XmrPrice { get; set; }

    [BindProperty] public string PortfolioName { get; set; } = "My Portfolio";

    public IndexModel(IPortfolioRepository portfolios, ICoinRepository coins)
    {
        _portfolios = portfolios;
        _coins = coins;
    }

    private int GetUserId() => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public async Task OnGetAsync()
    {
        var userId = GetUserId();
        Portfolios = await _portfolios.GetByUserIdAsync(userId);
        TotalNetValue = await _portfolios.GetUserTotalValueUsdAsync(userId);

        var allCoins = await _coins.GetAllAsync();
        var xmr = allCoins.FirstOrDefault(c => c.Symbol.ToUpper() == "XMR");
        XmrPrice = xmr?.PriceUsd ?? 0;
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
}
