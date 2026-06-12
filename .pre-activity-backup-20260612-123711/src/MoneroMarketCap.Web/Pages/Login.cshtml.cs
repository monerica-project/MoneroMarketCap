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
        var user = await _users.GetByUsernameAsync(Username);

        if (user == null || !BCrypt.Net.BCrypt.Verify(Password, user.PasswordHash))
        {
            Error = "Invalid credentials.";
            return Page();
        }

        var claims = new List<Claim>
    {
        new(ClaimTypes.Name, user.Username),
        new(ClaimTypes.NameIdentifier, user.Id.ToString())
    };

        foreach (var ur in user.UserRoles)
            claims.Add(new Claim(ClaimTypes.Role, ur.Role.Name));

 

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