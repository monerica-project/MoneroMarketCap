using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MoneroMarketCap.Web.Helpers;

namespace MoneroMarketCap.Pages;

/// <summary>
/// Sets the user's display currency cookie and redirects back to the page they
/// were on. Anonymous-friendly (cookie-based, no DB write).
/// </summary>
public class SetCurrencyModel : PageModel
{
    public IActionResult OnPost(string? code, string? returnUrl = null)
    {
        if (!string.IsNullOrWhiteSpace(code))
            CurrencyResolver.WriteCookie(HttpContext, code);

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);

        return RedirectToPage("/Index");
    }

    // Allow GET fallback for browsers without JS that may follow a link.
    public IActionResult OnGet(string? code, string? returnUrl = null)
    {
        if (!string.IsNullOrWhiteSpace(code))
            CurrencyResolver.WriteCookie(HttpContext, code);

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);

        return RedirectToPage("/Index");
    }
}
