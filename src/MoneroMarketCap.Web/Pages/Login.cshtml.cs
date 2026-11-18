using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MoneroMarketCap.Data.Repositories;
using System.Security.Claims;

public class LoginModel : PageModel
{
    private readonly IUserRepository _users;

    [BindProperty] public string Username { get; set; } = string.Empty;
    [BindProperty] public string Password { get; set; } = string.Empty;
    public string? Error { get; set; }

    public LoginModel(IUserRepository users) => _users = users;

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        // A sign-in name longer than any real one is refused outright (it was also stored as a key).
        if ((Username ?? string.Empty).Length > 200 || (Password ?? string.Empty).Length > 500)
        {
            Error = "Invalid credentials.";
            return Page();
        }

        var nameKey = MoneroMarketCap.Web.Services.SafetyLimits.SignInKey(Username);
        if (string.IsNullOrEmpty(Username) || string.IsNullOrEmpty(Password)
            || MoneroMarketCap.Web.Services.SafetyLimits.Reached("signin", MoneroMarketCap.Web.Services.SafetyLimits.FailedSignInsPer10Min, TimeSpan.FromMinutes(10))
            || MoneroMarketCap.Web.Services.SafetyLimits.Reached(nameKey, MoneroMarketCap.Web.Services.SafetyLimits.FailedSignInsPerNamePer10Min, TimeSpan.FromMinutes(10)))
        {
            Error = string.IsNullOrEmpty(Username) || string.IsNullOrEmpty(Password)
                ? "Invalid credentials."
                : "Too many failed sign-in attempts. Please wait 10 minutes and try again.";
            return Page();
        }

        var user = await _users.GetByUsernameAsync(Username);

        if (user == null || !BCrypt.Net.BCrypt.Verify(Password, user.PasswordHash))
        {
            MoneroMarketCap.Web.Services.SafetyLimits.Mark("signin");
            MoneroMarketCap.Web.Services.SafetyLimits.Mark(nameKey);
            Error = "Invalid credentials.";
            return Page();
        }

        // Record the successful login so the admin activity dashboard can bucket
        // users by recency. The user entity is tracked by the repository's context.
        user.LastLoginAt = DateTime.UtcNow;
        await _users.SaveChangesAsync();

        var claims = new List<Claim>
    {
        new(ClaimTypes.Name, user.Username),
        new(ClaimTypes.NameIdentifier, user.Id.ToString())
    };

        foreach (var ur in user.UserRoles)
            claims.Add(new Claim(ClaimTypes.Role, ur.Role.Name));

        claims.Add(new Claim(
            MoneroMarketCap.Web.Services.CookiePrincipalValidator.PasswordStampClaim,
            MoneroMarketCap.Web.Services.CookiePrincipalValidator.PasswordStamp(user.PasswordHash)));

 

        await HttpContext.SignInAsync("CookieAuth",
    new ClaimsPrincipal(new ClaimsIdentity(claims, "CookieAuth")),
    new AuthenticationProperties
    {
        IsPersistent = true,
        ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30)
    });

        return Redirect("/Portfolios/Index");
    }
}