using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data;

namespace MoneroMarketCap.Pages.Admin;

/// <summary>
/// Admin-only user activity overview: total registered users, how many have logged
/// in within rolling time windows (24h / 7d / 30d / 90d), how many have never logged
/// in, and recent signups over the same windows. Login recency is read from
/// <c>AppUser.LastLoginAt</c>, stamped by the login handler on each successful sign-in.
/// </summary>
[Authorize(Policy = "AdminOnly")]
public class ActivityModel : PageModel
{
    private readonly AppDbContext _db;

    public ActivityModel(AppDbContext db) => _db = db;

    public int TotalUsers { get; set; }
    public int NeverLoggedIn { get; set; }

    public IReadOnlyList<Bucket> LoginBuckets { get; set; } = new List<Bucket>();
    public IReadOnlyList<Bucket> SignupBuckets { get; set; } = new List<Bucket>();
    public IReadOnlyList<RecentLogin> RecentLogins { get; set; } = new List<RecentLogin>();

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        var now = DateTime.UtcNow;

        // Pull just the timestamps we need for every user in one read, then bucket
        // in memory. Keeps this to a single query regardless of how many windows
        // we report on.
        var users = await _db.Users
            .AsNoTracking()
            .Select(u => new UserTimes
            {
                Username = u.Username,
                CreatedAt = u.CreatedAt,
                LastLoginAt = u.LastLoginAt,
            })
            .ToListAsync(ct);

        TotalUsers = users.Count;
        NeverLoggedIn = users.Count(u => u.LastLoginAt == null);

        var windows = new (string Label, TimeSpan Span)[]
        {
            ("Last 24 hours", TimeSpan.FromHours(24)),
            ("Last 7 days", TimeSpan.FromDays(7)),
            ("Last 30 days", TimeSpan.FromDays(30)),
            ("Last 90 days", TimeSpan.FromDays(90)),
        };

        // "Active" = logged in at or after the window's start.
        LoginBuckets = windows
            .Select(w => new Bucket
            {
                Label = w.Label,
                Count = users.Count(u => u.LastLoginAt >= now - w.Span),
            })
            .ToList();

        // New signups within the same windows, for context next to the login numbers.
        SignupBuckets = windows
            .Select(w => new Bucket
            {
                Label = w.Label,
                Count = users.Count(u => u.CreatedAt >= now - w.Span),
            })
            .ToList();

        RecentLogins = users
            .Where(u => u.LastLoginAt != null)
            .OrderByDescending(u => u.LastLoginAt)
            .Take(25)
            .Select(u => new RecentLogin
            {
                Username = u.Username,
                LastLoginAt = u.LastLoginAt!.Value,
                CreatedAt = u.CreatedAt,
            })
            .ToList();
    }

    private class UserTimes
    {
        public string Username { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime? LastLoginAt { get; set; }
    }

    public class Bucket
    {
        public string Label { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    public class RecentLogin
    {
        public string Username { get; set; } = string.Empty;
        public DateTime LastLoginAt { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
