using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Models;
using System.Security.Claims;

namespace MoneroMarketCap.Pages.Portfolios;

[Authorize]
public class TransactionsModel : PageModel
{
    private readonly AppDbContext _db;

    public PortfolioCoin? PortfolioCoin { get; set; }
    public List<CoinTransaction> Transactions { get; set; } = new();

    public TransactionsModel(AppDbContext db) => _db = db;

    private int GetUserId() => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public async Task<IActionResult> OnGetAsync(int portfolioCoinId)
    {
        PortfolioCoin = await _db.PortfolioCoins
            .Include(pc => pc.Coin)
            .Include(pc => pc.Portfolio)
            .Include(pc => pc.Transactions)
            .FirstOrDefaultAsync(pc => pc.Id == portfolioCoinId);

        if (PortfolioCoin == null || PortfolioCoin.Portfolio.UserId != GetUserId())
            return Forbid();

        Transactions = PortfolioCoin.Transactions
            .OrderByDescending(t => t.TransactedAt)
            .ToList();

        return Page();
    }
}