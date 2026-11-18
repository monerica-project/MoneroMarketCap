using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

public class LogoutModel : PageModel
{
    // Signing out is a POST (with the anti-forgery token), so a link on another site cannot
    // sign a member out. A plain visit to /logout just goes home.
    public IActionResult OnGet() => Redirect("/");

    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync("CookieAuth");
        return Redirect("/Login");
    }
}