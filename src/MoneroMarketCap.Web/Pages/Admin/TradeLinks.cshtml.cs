using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Models;

namespace MoneroMarketCap.Pages.Admin;

[Authorize(Policy = "AdminOnly")]
public class TradeLinksModel : PageModel
{
    private readonly AppDbContext _db;

    public TradeLinksModel(AppDbContext db) => _db = db;

    // Search term (also round-trips through POSTs so the view doesn't reset).
    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    // Every coin that already has a trade link — always shown, regardless of search.
    public IReadOnlyList<Coin> Configured { get; set; } = new List<Coin>();

    // Search matches (or top coins when no query), excluding the configured ones above.
    public IReadOnlyList<Coin> Results { get; set; } = new List<Coin>();

    public async Task OnGetAsync()
    {
        Configured = await _db.Coins
            .Where(c => c.TradeUrl != null && c.TradeUrl != "")
            .OrderBy(c => c.MarketCapRank == 0 ? int.MaxValue : c.MarketCapRank)
            .ToListAsync();

        var configuredIds = Configured.Select(c => c.Id).ToHashSet();

        var query = _db.Coins.AsQueryable();
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var term = Q.Trim();
            query = query.Where(c =>
                EF.Functions.ILike(c.Symbol, $"%{term}%") ||
                EF.Functions.ILike(c.Name, $"%{term}%") ||
                EF.Functions.ILike(c.CoinGeckoId, $"%{term}%"));
        }

        Results = await query
            .Where(c => !configuredIds.Contains(c.Id))
            .OrderBy(c => c.MarketCapRank == 0 ? int.MaxValue : c.MarketCapRank)
            .Take(50)
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostSetLinkAsync(int id, string? tradeUrl)
    {
        var coin = await _db.Coins.FindAsync(id);
        if (coin == null)
        {
            TempData["Error"] = "Coin not found.";
            return RedirectToPage(new { q = Q });
        }

        var url = tradeUrl?.Trim();

        // Empty input = clear the link (same as the Clear button).
        if (string.IsNullOrEmpty(url))
        {
            coin.TradeUrl = null;
            coin.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            TempData["Status"] = $"Cleared trade link for {coin.Symbol}.";
            return RedirectToPage(new { q = Q });
        }

        // Only accept absolute http(s) URLs so a bad paste can't render a broken button.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            TempData["Error"] = $"\"{url}\" is not a valid http(s) URL — not saved.";
            return RedirectToPage(new { q = Q });
        }

        coin.TradeUrl = url;
        coin.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        TempData["Status"] = $"Saved trade link for {coin.Symbol}.";
        return RedirectToPage(new { q = Q });
    }

    public async Task<IActionResult> OnPostClearLinkAsync(int id)
    {
        var coin = await _db.Coins.FindAsync(id);
        if (coin != null)
        {
            coin.TradeUrl = null;
            coin.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            TempData["Status"] = $"Cleared trade link for {coin.Symbol}.";
        }
        return RedirectToPage(new { q = Q });
    }
}
