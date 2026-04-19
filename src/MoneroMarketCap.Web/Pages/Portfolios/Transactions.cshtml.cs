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
public class TransactionsModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly IPortfolioRepository _portfolios;

    public PortfolioCoin? PortfolioCoin { get; set; }
    public List<CoinTransaction> Transactions { get; set; } = new();
    public decimal CoinCurrentPrice { get; set; }

    [BindProperty] public TransactionType TransactionType { get; set; }
    [BindProperty] public decimal Amount { get; set; }
    [BindProperty] public decimal PriceUsdAtTime { get; set; }
    [BindProperty] public string? Notes { get; set; }
    public bool PrivacyMode { get; set; }

    public TransactionsModel(AppDbContext db, IPortfolioRepository portfolios)
    {
        _db = db;
        _portfolios = portfolios;
    }

    private int GetUserId() => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private async Task LoadPrivacyModeAsync()
    {
        PrivacyMode = await _db.Users
            .Where(u => u.Id == GetUserId())
            .Select(u => u.PrivacyMode)
            .FirstOrDefaultAsync();
    }

    private async Task<bool> LoadPortfolioCoin(int portfolioCoinId)
    {
        PortfolioCoin = await _db.PortfolioCoins
            .Include(pc => pc.Coin)
            .Include(pc => pc.Portfolio)
            .Include(pc => pc.Transactions)
            .FirstOrDefaultAsync(pc => pc.Id == portfolioCoinId);

        if (PortfolioCoin == null || PortfolioCoin.Portfolio.UserId != GetUserId())
            return false;

        Transactions = PortfolioCoin.Transactions
            .OrderByDescending(t => t.TransactedAt)
            .ToList();

        CoinCurrentPrice = PortfolioCoin.Coin.PriceUsd;
        return true;
    }

    public async Task<IActionResult> OnGetAsync(int portfolioCoinId)
    {
        if (!await LoadPortfolioCoin(portfolioCoinId))
            return Forbid();

        await LoadPrivacyModeAsync();
        PriceUsdAtTime = CoinCurrentPrice;
        return Page();
    }

    public async Task<IActionResult> OnPostAddAsync(int portfolioCoinId)
    {
        if (!await LoadPortfolioCoin(portfolioCoinId))
            return Forbid();

        await _portfolios.AddTransactionAsync(
            PortfolioCoin!.PortfolioId,
            PortfolioCoin.CoinId,
            TransactionType,
            Amount,
            PriceUsdAtTime,
            Notes);

        return RedirectToPage(new { portfolioCoinId });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int portfolioCoinId, int transactionId)
    {
        if (!await LoadPortfolioCoin(portfolioCoinId))
            return Forbid();

        var tx = Transactions.FirstOrDefault(t => t.Id == transactionId);
        if (tx == null)
            return NotFound();

        var portfolioId = PortfolioCoin!.PortfolioId;
        var isLastTransaction = Transactions.Count == 1;

        await _portfolios.DeleteTransactionAsync(transactionId);

        if (isLastTransaction)
            return RedirectToPage("/Portfolios/Detail", new { id = portfolioId });

        return RedirectToPage(new { portfolioCoinId });
    }
}