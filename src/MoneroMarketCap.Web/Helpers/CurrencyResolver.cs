using Microsoft.AspNetCore.Http;
using MoneroMarketCap.Services.Models;

namespace MoneroMarketCap.Web.Helpers;

/// <summary>
/// Resolves which display currency the current HTTP request should use, in this
/// priority order:
///   1. <c>?currency=xxx</c> query string
///   2. <c>mmc_currency</c> cookie
///   3. Default (USD)
///
/// Whichever wins, falls back to USD if the code isn't in <see cref="CurrencyCatalog"/>.
/// </summary>
public static class CurrencyResolver
{
    public const string CookieName = "mmc_currency";
    public const string QueryKey = "currency";

    public static CurrencyInfo Resolve(HttpContext ctx)
    {
        if (ctx.Request.Query.TryGetValue(QueryKey, out var q)
            && CurrencyCatalog.IsSupported(q.ToString()))
        {
            return CurrencyCatalog.Get(q.ToString());
        }

        if (ctx.Request.Cookies.TryGetValue(CookieName, out var cookieVal)
            && CurrencyCatalog.IsSupported(cookieVal))
        {
            return CurrencyCatalog.Get(cookieVal);
        }

        return CurrencyCatalog.Default;
    }

    public static void WriteCookie(HttpContext ctx, string code)
    {
        var safe = CurrencyCatalog.IsSupported(code) ? code.ToUpperInvariant() : CurrencyCatalog.DefaultCode;
        ctx.Response.Cookies.Append(CookieName, safe, new CookieOptions
        {
            Expires = DateTimeOffset.UtcNow.AddYears(1),
            SameSite = SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps,
            HttpOnly = false,   // not sensitive; readable from client is fine
            IsEssential = true, // pure UI preference, no consent gate
        });
    }
}
