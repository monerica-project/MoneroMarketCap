using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data;
using System.Security.Claims;

namespace MoneroMarketCap.Pages;

[Authorize]
public class DeleteAccountModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ILogger<DeleteAccountModel> _logger;

    [BindProperty] public string CurrentPassword { get; set; } = string.Empty;
    [BindProperty] public string ConfirmText { get; set; } = string.Empty;

    public string? Error { get; set; }

    public DeleteAccountModel(AppDbContext db, ILogger<DeleteAccountModel> logger)
    {
        _db = db;
        _logger = logger;
    }

    private int GetUserId() => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        if (ConfirmText != "DELETE")
        {
            Error = "Please type DELETE exactly to confirm.";
            return Page();
        }

        var userId = GetUserId();

        var user = await _db.Users
            .Include(u => u.UserRoles)
            .Include(u => u.Portfolios)
                .ThenInclude(p => p.PortfolioCoins)
                    .ThenInclude(pc => pc.Transactions)
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user == null)
            return NotFound();

        if (!BCrypt.Net.BCrypt.Verify(CurrentPassword, user.PasswordHash))
        {
            Error = "Current password is incorrect.";
            return Page();
        }

        var username = user.Username;

        using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            // 1. Transactions (bottom of the tree)
            var allTransactions = user.Portfolios
                .SelectMany(p => p.PortfolioCoins)
                .SelectMany(pc => pc.Transactions)
                .ToList();
            if (allTransactions.Count > 0)
            {
                _db.CoinTransactions.RemoveRange(allTransactions);
                await _db.SaveChangesAsync();
            }

            // 2. PortfolioCoins
            var allPortfolioCoins = user.Portfolios
                .SelectMany(p => p.PortfolioCoins)
                .ToList();
            if (allPortfolioCoins.Count > 0)
            {
                _db.PortfolioCoins.RemoveRange(allPortfolioCoins);
                await _db.SaveChangesAsync();
            }

            // 3. Portfolios
            if (user.Portfolios.Count > 0)
            {
                _db.Portfolios.RemoveRange(user.Portfolios);
                await _db.SaveChangesAsync();
            }

            // 4. Role assignments
            if (user.UserRoles.Count > 0)
            {
                _db.UserRoles.RemoveRange(user.UserRoles);
                await _db.SaveChangesAsync();
            }

            // 5. The user itself
            _db.Users.Remove(user);
            await _db.SaveChangesAsync();

            await tx.CommitAsync();

            _logger.LogInformation("User {UserId} ({Username}) deleted their account", userId, username);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            _logger.LogError(ex, "Failed to delete account for user {UserId}", userId);
            Error = "An error occurred while deleting your account. Please try again.";
            return Page();
        }

        // Sign out and bounce to home
        await HttpContext.SignOutAsync(MoneroMarketCap.Data.Constants.AuthSchemes.Cookie);

        TempData["AccountDeleted"] = $"Account '{username}' has been permanently deleted.";
        return RedirectToPage("/Index");
    }
}