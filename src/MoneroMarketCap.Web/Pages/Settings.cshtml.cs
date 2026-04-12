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

        Success = true;
        return Page();
    }
}
