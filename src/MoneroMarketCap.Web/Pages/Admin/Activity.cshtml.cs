using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data;

namespace MoneroMarketCap.Pages.Admin;

/// <summary>
/// Admin-only user activity overview. Activity is driven by <c>AppUser.LastSeenAt</c>
/// (updated on a throttled basis by LastSeenMiddleware on every authenticated request),
/// so it reflects ongoing usage by already-signed-in users — not just fresh logins.
/// Signup counts come from <c>CreatedAt</c>, which has always been recorded, so the
/// signup figures are accurate from day one while the activity figures fill in as
/// users visit after this feature is deployed.
/// </summary>
[Authorize(Policy = "AdminOnly")]
public class ActivityModel : PageModel
{
    private readonly AppDbContext _db;

    public ActivityModel(AppDbContext db) => _db = db;

    public int TotalUsers { get; set; }
    public int ActiveEver { get; set; }
    public int NeverActive { get; set; }
    public int ActiveLast7Days { get; set; }
    public int NewLast7Days { get; set; }

    /// <summary>Earliest recorded activity — a proxy for "tracking started" — or null if none yet.</summary>
    public DateTime? TrackingSince { get; set; }

    public IReadOnlyList<Bucket> ActivityBuckets { get; set; } = new List<Bucket>();
    public IReadOnlyList<Bucket> SignupBuckets { get; set; } = new List<Bucket>();
    public IReadOnlyList<RecentRow> RecentActivity { get; set; } = new List<RecentRow>();

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        var now = DateTime.UtcNow;

        // One read of just the timestamps we need; everything is bucketed in memory.
        var users = await _db.Users
            .AsNoTracking()
            .Select(u => new UserTimes
            {
                Username = u.Username,
                CreatedAt = u.CreatedAt,
                LastLoginAt = u.LastLoginAt,
                LastSeenAt = u.LastSeenAt,
            })
            .ToListAsync(ct);

        TotalUsers = users.Count;
        ActiveEver = users.Count(u => u.LastSeenAt != null);
        NeverActive = users.Count(u => u.LastSeenAt == null);
        TrackingSince = users.Where(u => u.LastSeenAt != null).Select(u => u.LastSeenAt).Min();

        var windows = new (string Label, TimeSpan Span)[]
        {
            ("Last 24 hours", TimeSpan.FromHours(24)),
            ("Last 7 days", TimeSpan.FromDays(7)),
            ("Last 30 days", TimeSpan.FromDays(30)),
            ("Last 90 days", TimeSpan.FromDays(90)),
        };

        // "Active" = seen (visited a page while signed in) at or after the window start.
        ActivityBuckets = windows
            .Select(w => new Bucket
            {
                Label = w.Label,
                Count = users.Count(u => u.LastSeenAt >= now - w.Span),
            })
            .ToList();

        SignupBuckets = windows
            .Select(w => new Bucket
            {
                Label = w.Label,
                Count = users.Count(u => u.CreatedAt >= now - w.Span),
            })
            .ToList();

        ActiveLast7Days = ActivityBuckets.First(b => b.Label == "Last 7 days").Count;
        NewLast7Days = SignupBuckets.First(b => b.Label == "Last 7 days").Count;

        RecentActivity = users
            .Where(u => u.LastSeenAt != null || u.LastLoginAt != null)
            .OrderByDescending(u => u.LastSeenAt ?? u.LastLoginAt)
            .Take(25)
            .Select(u => new RecentRow
            {
                Username = u.Username,
                LastSeenAt = u.LastSeenAt,
                LastLoginAt = u.LastLoginAt,
                CreatedAt = u.CreatedAt,
            })
            .ToList();
    }

    private class UserTimes
    {
        public string Username { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime? LastLoginAt { get; set; }
        public DateTime? LastSeenAt { get; set; }
    }

    public class Bucket
    {
        public string Label { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    public class RecentRow
    {
        public string Username { get; set; } = string.Empty;
        public DateTime? LastSeenAt { get; set; }
        public DateTime? LastLoginAt { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
