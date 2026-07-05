using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data;

namespace MoneroMarketCap.Pages.Exchanges;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;

    public IndexModel(AppDbContext db) => _db = db;

    public const int PageSize = 60;
    public int PageNum { get; set; } = 1;
    public int Total { get; set; }
    public int TotalPages => (int)Math.Ceiling(Total / (double)PageSize);
    public List<Row> Rows { get; set; } = new();

    public record Row(string Slug, string Name, string? Grade, string? Kind, int CoinCount);

    public async Task<IActionResult> OnGetAsync(int p = 1)
    {
        Total = await _db.Exchanges.CountAsync(HttpContext.RequestAborted);
        PageNum = Math.Clamp(p, 1, Math.Max(1, TotalPages));
        Rows = await _db.Exchanges.AsNoTracking()
            .OrderBy(e => e.Name)
            .Skip((PageNum - 1) * PageSize)
            .Take(PageSize)
            .Select(e => new Row(e.Slug, e.Name, e.Grade, e.Kind, e.ExchangeCoins.Count))
            .ToListAsync(HttpContext.RequestAborted);
        return Page();
    }
}
