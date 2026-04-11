using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Models;
using System.Security.Claims;

[Authorize]
public class PortfolioModel : PageModel
{
    private readonly AppDbContext _db;
    public List<Portfolio> Entries { get; set; } = new();
    public List<Coin> AllCoins { get; set; } = new();

    [BindProperty] public int CoinId { get; set; }
    [BindProperty] public decimal Amount { get; set; }

    public PortfolioModel(AppDbContext db) => _db = db;

    public async Task OnGetAsync()
    {
        
    }

    public async Task<IActionResult> OnPostAsync()
    {
        
        return RedirectToPage();
    }
}