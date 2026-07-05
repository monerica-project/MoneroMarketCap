using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Models;

namespace MoneroMarketCap.Pages.Exchanges;

public class DetailModel : PageModel
{
    private readonly AppDbContext _db;

    public DetailModel(AppDbContext db) => _db = db;

    public Exchange Exchange { get; set; } = default!;

    /// <summary>Where the "go to exchange" button points (affiliate if present, else the site).</summary>
    public string GoUrl { get; set; } = string.Empty;
    public bool GoIsAffiliate { get; set; }

    public const int CoinPageSize = 48;
    public int CoinPage { get; set; } = 1;
    public int CoinTotal { get; set; }
    public int CoinTotalPages => (int)Math.Ceiling(CoinTotal / (double)CoinPageSize);
    public List<CoinRow> Coins { get; set; } = new();

    public record CoinRow(string Symbol, string Name);

    public async Task<IActionResult> OnGetAsync(string slug, int cp = 1)
    {
        var ex = await _db.Exchanges.AsNoTracking()
            .Include(e => e.Contacts)
            .FirstOrDefaultAsync(e => e.Slug == slug, HttpContext.RequestAborted);
        if (ex is null)
        {
            return NotFound();
        }

        Exchange = ex;
        GoIsAffiliate = !string.IsNullOrWhiteSpace(ex.AffiliateUrl);
        GoUrl = GoIsAffiliate ? ex.AffiliateUrl! : ex.WebsiteUrl;

        CoinTotal = await _db.ExchangeCoins.CountAsync(ec => ec.ExchangeId == ex.Id, HttpContext.RequestAborted);
        CoinPage = Math.Clamp(cp, 1, Math.Max(1, CoinTotalPages));
        Coins = await _db.ExchangeCoins.AsNoTracking()
            .Where(ec => ec.ExchangeId == ex.Id)
            .Select(ec => ec.Coin!)
            .OrderBy(c => c.Symbol)
            .Skip((CoinPage - 1) * CoinPageSize)
            .Take(CoinPageSize)
            .Select(c => new CoinRow(c.Symbol, c.Name))
            .ToListAsync(HttpContext.RequestAborted);

        return Page();
    }

    /// <summary>mailto: for emails, the raw value for URLs, otherwise null (plain text).</summary>
    public static string? ContactHref(ExchangeContact c)
    {
        var v = (c.Value ?? string.Empty).Trim();
        if (v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            v.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return v;
        }

        if (string.Equals(c.Type, "Email", StringComparison.OrdinalIgnoreCase) && v.Contains('@'))
        {
            return "mailto:" + v;
        }

        return null;
    }
}
