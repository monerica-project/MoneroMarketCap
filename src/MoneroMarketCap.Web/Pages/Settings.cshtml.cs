using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MoneroMarketCap.Data;
using System.Security.Claims;

namespace MoneroMarketCap.Pages;

[Authorize]
public class SettingsModel : PageModel
{
    private readonly AppDbContext _db;

    [BindProperty] public string CurrentPassword { get; set; } = string.Empty;
    [BindProperty] public string NewPassword { get; set; } = string.Empty;
    [BindProperty] public string ConfirmPassword { get; set; } = string.Empty;

    public string? Error { get; set; }
    public bool Success { get; set; }

    public SettingsModel(AppDbContext db) => _db = db;

    private int GetUserId() => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(NewPassword) || NewPassword.Length < 8)
        {
            Error = "New password must be at least 8 characters.";
            return Page();
        }

        if (NewPassword != ConfirmPassword)
        {
            Error = "New passwords do not match.";
            return Page();
        }

        var user = await _db.Users.FindAsync(GetUserId());
        if (user == null)
            return NotFound();

        if (!BCrypt.Net.BCrypt.Verify(CurrentPassword, user.PasswordHash))
        {
            Error = "Current password is incorrect.";
            return Page();
        }

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(NewPassword);
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Every other signed-in session is now refused (its cookie carries the old password
        // stamp); this one is re-issued with the new stamp so the user stays signed in here.
        var cache = HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
        MoneroMarketCap.Web.Services.CookiePrincipalValidator.Invalidate(cache, user.Id);
        var roles = await _db.Users.Where(u => u.Id == user.Id)
            .SelectMany(u => u.UserRoles.Select(ur => ur.Role.Name))
            .ToListAsync();
        await HttpContext.SignInAsync(
            "CookieAuth",
            MoneroMarketCap.Web.Services.CookiePrincipalValidator.BuildPrincipal(
                new MoneroMarketCap.Web.Services.CookiePrincipalValidator.UserSnapshot(
                    user.Id,
                    user.Username,
                    roles,
                    MoneroMarketCap.Web.Services.CookiePrincipalValidator.PasswordStamp(user.PasswordHash)),
                "CookieAuth"),
            new Microsoft.AspNetCore.Authentication.AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) });

        Success = true;
        return Page();
    }
}
