using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Data.Repositories;

public class RegisterModel : PageModel
{
    private readonly IUserRepository _users;
    public string? GeneratedUsername { get; set; }
    public string? GeneratedPassword { get; set; }
    public string CaptchaQuestion { get; set; } = string.Empty;
    public string? CaptchaError { get; set; }

    [BindProperty]
    public int CaptchaAnswer { get; set; }

    public RegisterModel(IUserRepository users) => _users = users;

    public void OnGet()
    {
        GenerateCaptcha();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var expected = TempData["CaptchaAnswer"] as int?;

        if (expected == null || CaptchaAnswer != expected.Value)
        {
            CaptchaError = "Incorrect answer, please try again.";
            GenerateCaptcha();
            return Page();
        }

        GeneratedUsername = "user_" + Guid.NewGuid().ToString("N")[..10];
        GeneratedPassword = Guid.NewGuid().ToString("N")[..16];

        var user = new AppUser
        {
            Username = GeneratedUsername,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(GeneratedPassword)
        };

        await _users.AddAsync(user);
        await _users.SaveChangesAsync();

        return Page();
    }

    private void GenerateCaptcha()
    {
        var rng = Random.Shared;
        int a = rng.Next(2, 20);
        int b = rng.Next(2, 20);
        CaptchaQuestion = $"What is {a} + {b}?";
        TempData["CaptchaAnswer"] = a + b;
        TempData.Keep("CaptchaAnswer"); // persist across the GET so it survives to POST
    }
}