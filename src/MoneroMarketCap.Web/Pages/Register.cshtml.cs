using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Data.Repositories;
using MoneroMarketCap.Web.Services;
using System.Security.Cryptography;

public class RegisterModel : PageModel
{
    private readonly IUserRepository _users;
    private readonly ICaptchaStore _captcha;
    public string? GeneratedUsername { get; set; }
    public string? GeneratedPassword { get; set; }
    public string CaptchaQuestion { get; set; } = string.Empty;
    public string? CaptchaError { get; set; }

    [BindProperty]
    public int CaptchaAnswer { get; set; }

    /// <summary>Opaque one-time token referencing the server-side answer (see ICaptchaStore).</summary>
    [BindProperty]
    public string? CaptchaToken { get; set; }

    public RegisterModel(IUserRepository users, ICaptchaStore captcha)
    {
        _users = users;
        _captcha = captcha;
    }

    public void OnGet()
    {
        GenerateCaptcha();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        // One-time check: the token is consumed whether or not the answer is right.
        if (!_captcha.TryConsume(CaptchaToken, CaptchaAnswer))
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
        int a = RandomNumberGenerator.GetInt32(2, 20);
        int b = RandomNumberGenerator.GetInt32(2, 20);
        CaptchaQuestion = $"What is {a} + {b}?";

        // The answer stays on the server; the form only carries the token.
        CaptchaToken = _captcha.Create(a + b);
    }
}