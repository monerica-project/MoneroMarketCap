using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MoneroMarketCap.Data;

namespace MoneroMarketCap.Web.Middleware;

/// <summary>
/// Updates <c>AppUser.LastSeenAt</c> for the current authenticated user on each
/// request, throttled so the database is written at most once per user per
/// <see cref="Throttle"/> window. A successful write seeds an in-memory cache key
/// that suppresses further writes until it expires, so the steady-state cost is a
/// single cache lookup per request and one UPDATE per active user every few minutes.
///
/// The write uses ExecuteUpdateAsync (a direct UPDATE) rather than loading and
/// saving the entity, so it never touches change tracking and never bumps the
/// user's UpdatedAt audit column.
///
/// Note: the throttle cache is per-process. On a single-instance deployment that's
/// exact; behind multiple instances each would keep its own window, at worst
/// producing a few extra writes — never stale or lost data.
/// </summary>
public class LastSeenMiddleware
{
    private static readonly TimeSpan Throttle = TimeSpan.FromMinutes(5);

    private readonly RequestDelegate _next;

    public LastSeenMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, AppDbContext db, IMemoryCache cache)
    {
        // Run the request first so the "last seen" write never adds latency to the
        // response the user is waiting on.
        await _next(context);

        if (context.User?.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var idClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(idClaim, out var userId))
        {
            return;
        }

        var cacheKey = $"lastseen:{userId}";
        if (cache.TryGetValue(cacheKey, out _))
        {
            return; // Seen within the throttle window — skip the DB entirely.
        }

        // Reserve the window before writing so concurrent requests don't all write.
        cache.Set(cacheKey, true, Throttle);

        var now = DateTime.UtcNow;
        try
        {
            await db.Users
                .Where(u => u.Id == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSeenAt, now), context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected mid-request; the timestamp simply updates next time.
        }
    }
}
