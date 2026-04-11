using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Data.Repositories;
using System.Security.Claims;

namespace MoneroMarketCap.Pages.Portfolios;

[Authorize]
public class DetailModel : PageModel
{
    private readonly IPortfolioRepository _portfolios;
    private readonly ICoinRepository _coins;

    public Portfolio? Portfolio { get; set; }
    public List<SelectListItem> CoinOptions { get; set; } = new();

    [BindProperty] public int CoinId { get; set; }
    [BindProperty] public TransactionType TransactionType { get; set; }
    [BindProperty] public decimal Amount { get; set; }
    [BindProperty] public decimal PriceUsdAtTime { get; set; }
    [BindProperty] public string? Notes { get; set; }

    public DetailModel(IPortfolioRepository portfolios, ICoinRepository coins)
    {
        _portfolios = portfolios;
        _coins = coins;
    }

    private int GetUserId() => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public async Task<IActionResult> OnGetAsync(int id)
    {
        Portfolio = await _portfolios.GetWithDetailsAsync(id);
        if (Portfolio == null || Portfolio.UserId != GetUserId())
            return Forbid();

        await LoadCoinOptions();
        return Page();
    }

    public async Task<IActionResult> OnPostAddTransactionAsync(int id)
    {
        Portfolio = await _portfolios.GetWithDetailsAsync(id);
        if (Portfolio == null || Portfolio.UserId != GetUserId())
            return Forbid();

        await _portfolios.AddTransactionAsync(id, CoinId, TransactionType, Amount, PriceUsdAtTime, Notes);

        return RedirectToPage(new { id });
    }

    private async Task LoadCoinOptions()
    {
        var coins = await _coins.GetAllAsync();
        CoinOptions = coins.Select(c => new SelectListItem($"{c.Symbol} - {c.Name}", c.Id.ToString())).ToList();
    }
}