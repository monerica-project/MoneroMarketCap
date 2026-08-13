using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using MoneroMarketCap.Services.Models;
using MoneroMarketCap.Web.Helpers;

namespace MoneroMarketCap.Pages;

/// <summary>
/// Sets the user's display currency cookie and redirects back to the page they
/// were on — with <c>?currency=CODE</c> added to the URL so the chosen currency is
/// bookmarkable and shareable. Anonymous-friendly (cookie-based, no DB write).
/// </summary>
public class SetCurrencyModel : PageModel
{
    public IActionResult OnPost(string? code, string? returnUrl = null) => this.Apply(code, returnUrl);

    // Allow GET fallback for browsers without JS that may follow a link.
    public IActionResult OnGet(string? code, string? returnUrl = null) => this.Apply(code, returnUrl);

    private IActionResult Apply(string? code, string? returnUrl)
    {
        if (!string.IsNullOrWhiteSpace(code))
        {
            CurrencyResolver.WriteCookie(HttpContext, code);
        }

        var target = (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)) ? returnUrl : "/";

        // Put the chosen currency in the URL so the user can bookmark this exact view.
        if (!string.IsNullOrWhiteSpace(code) && CurrencyCatalog.IsSupported(code))
        {
            target = WithCurrency(target, code.ToUpperInvariant());
        }

        return Redirect(target);
    }

    /// <summary>Returns <paramref name="url"/> (a local path[?query]) with the <c>currency</c>
    /// query parameter set to <paramref name="code"/>, replacing any existing one.</summary>
    private static string WithCurrency(string url, string code)
    {
        var qi = url.IndexOf('?');
        var path = qi < 0 ? url : url[..qi];
        var query = qi < 0 ? string.Empty : url[(qi + 1)..];

        var pairs = new List<KeyValuePair<string, string?>>();
        foreach (var kv in QueryHelpers.ParseQuery(query))
        {
            if (string.Equals(kv.Key, CurrencyResolver.QueryKey, StringComparison.OrdinalIgnoreCase))
            {
                continue; // drop the old currency param; we re-add it below
            }

            foreach (var v in kv.Value)
            {
                pairs.Add(new KeyValuePair<string, string?>(kv.Key, v));
            }
        }

        pairs.Add(new KeyValuePair<string, string?>(CurrencyResolver.QueryKey, code));
        return QueryHelpers.AddQueryString(path, pairs);
    }
}
