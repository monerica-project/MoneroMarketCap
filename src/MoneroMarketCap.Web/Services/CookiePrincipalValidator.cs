using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MoneroMarketCap.Data;

namespace MoneroMarketCap.Web.Services;

/// <summary>
/// Re-checks the auth cookie against the database. Login bakes the username and roles
/// into a long-lived sliding cookie; without this, deleting a user or revoking a role
/// had no effect until the cookie expired. On every authenticated request the cookie's
/// principal is compared with a short-lived (<see cref="SnapshotTtl"/>) cached snapshot
/// of the user's current row:
///   * user gone → principal rejected and the cookie signed out;
///   * username/roles changed → principal replaced with fresh claims and the cookie
///     re-issued;
///   * otherwise untouched.
/// So a revocation takes effect within <see cref="SnapshotTtl"/>, at a cost of at most one
/// small indexed query per user per TTL per process.
/// </summary>
public static class CookiePrincipalValidator
{
    public static readonly TimeSpan SnapshotTtl = TimeSpan.FromMinutes(1);

    public enum Outcome
    {
        Valid,
        Reject,
        Replace,
    }

    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var services = context.HttpContext.RequestServices;
        var cache = services.GetRequiredService<IMemoryCache>();
        var idText = principal.FindFirstValue(ClaimTypes.NameIdentifier);

        UserSnapshot? snapshot = null;
        if (int.TryParse(idText, out var userId))
        {
            snapshot = await cache.GetOrCreateAsync(CacheKey(userId), async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = SnapshotTtl;
                var db = services.GetRequiredService<AppDbContext>();
                var row = await db.Users.AsNoTracking()
                    .Where(u => u.Id == userId)
                    .Select(u => new
                    {
                        u.Username,
                        u.PasswordHash,
                        Roles = u.UserRoles.Select(ur => ur.Role.Name).ToList(),
                    })
                    .FirstOrDefaultAsync(context.HttpContext.RequestAborted);

                return row == null ? null : new UserSnapshot(userId, row.Username, row.Roles, PasswordStamp(row.PasswordHash));
            });
        }

        switch (Decide(principal, snapshot))
        {
            case Outcome.Reject:
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(context.Scheme.Name);
                break;

            case Outcome.Replace:
                context.ReplacePrincipal(BuildPrincipal(snapshot!, context.Scheme.Name));
                context.ShouldRenew = true;
                break;
        }
    }

    /// <summary>Drops the cached snapshot so the next request re-reads the user (e.g. after a role change).</summary>
    public static void Invalidate(IMemoryCache cache, int userId) => cache.Remove(CacheKey(userId));

    /// <summary>Pure decision: compares the cookie principal with the user's current state.</summary>
    public static Outcome Decide(ClaimsPrincipal principal, UserSnapshot? current)
    {
        if (current == null)
        {
            // Missing/garbled id, or the user no longer exists.
            return Outcome.Reject;
        }

        // A password change signs out every other session: the cookie carries a stamp of the
        // password it was issued under. Cookies from before the stamp existed are re-issued once.
        var cookieStamp = principal.FindFirstValue(PasswordStampClaim);
        if (!string.IsNullOrEmpty(current.PasswordStamp))
        {
            if (string.IsNullOrEmpty(cookieStamp))
            {
                return Outcome.Replace;
            }

            if (!string.Equals(cookieStamp, current.PasswordStamp, StringComparison.Ordinal))
            {
                return Outcome.Reject;
            }
        }

        var cookieName = principal.FindFirstValue(ClaimTypes.Name);
        var cookieRoles = principal.FindAll(ClaimTypes.Role)
            .Select(c => c.Value)
            .ToHashSet(StringComparer.Ordinal);

        var same = string.Equals(cookieName, current.Username, StringComparison.Ordinal)
                   && cookieRoles.SetEquals(current.Roles);

        return same ? Outcome.Valid : Outcome.Replace;
    }

    /// <summary>The same claim set Login issues.</summary>
    public static ClaimsPrincipal BuildPrincipal(UserSnapshot user, string scheme)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        };
        claims.AddRange(user.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
        if (!string.IsNullOrEmpty(user.PasswordStamp))
        {
            claims.Add(new Claim(PasswordStampClaim, user.PasswordStamp));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme));
    }

    public const string PasswordStampClaim = "pwd";

    /// <summary>Short one-way stamp of the stored password hash; changes whenever the password does.</summary>
    public static string PasswordStamp(string? passwordHash) =>
        string.IsNullOrEmpty(passwordHash)
            ? string.Empty
            : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(passwordHash)))[..16];

    private static string CacheKey(int userId) => $"auth-user:{userId}";

    public sealed record UserSnapshot(int Id, string Username, IReadOnlyCollection<string> Roles, string PasswordStamp = "");
}
