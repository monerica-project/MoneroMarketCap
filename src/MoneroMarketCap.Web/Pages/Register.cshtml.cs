using BCrypt.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Data.Repositories;

public class RegisterModel : PageModel
{
    private readonly IUserRepository _users;
    public string? GeneratedUsername { get; set; }
    public string? GeneratedPassword { get; set; }

    public RegisterModel(IUserRepository users) => _users = users;

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
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
}