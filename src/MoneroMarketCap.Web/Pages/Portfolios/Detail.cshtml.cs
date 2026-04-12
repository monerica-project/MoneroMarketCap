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
    public Dictionary<int, decimal> CoinPrices { get; set; } = new();
    public decimal XmrPrice { get; set; }

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

    private async Task<bool> LoadPortfolio(int id)
    {
        Portfolio = await _portfolios.GetWithDetailsAsync(id);
        return Portfolio != null && Portfolio.UserId == GetUserId();
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        if (!await LoadPortfolio(id))
            return NotFound();

        await LoadCoinOptions();

        var coins = await _coins.GetAllAsync();
        var xmr = coins.FirstOrDefault(c => c.Symbol.ToUpper() == "XMR");
        if (xmr != null)
        {
            CoinId = xmr.Id;
            PriceUsdAtTime = xmr.PriceUsd;
            XmrPrice = xmr.PriceUsd;
        }

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
}
