using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MoneroMarketCap.Data;
using System.Security.Claims;

namespace MoneroMarketCap.Pages;

[Authorize]
public class TogglePrivacyModel : PageModel
{
    private readonly AppDbContext _db;
    public TogglePrivacyModel(AppDbContext db) => _db = db;

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var user = await _db.Users.FindAsync(userId);
        if (user == null) return NotFound();

        user.PrivacyMode = !user.PrivacyMode;
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Only accept local URLs to prevent open-redirect abuse
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);

        return RedirectToPage("/Portfolios/Index");
    }
}