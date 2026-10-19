namespace MoneroMarketCap.Data.Models;

public class AppUser : AuditableEntity
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;

    public ICollection<Portfolio> Portfolios { get; set; } = new List<Portfolio>();
    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();

    public bool IsInRole(string roleName) =>
        UserRoles.Any(ur => ur.Role?.Name == roleName);

    public bool PrivacyMode { get; set; } = false;

    /// <summary>
    /// Time range of the "Portfolio Value Over Time" chart: "all" (default), "1y" or "1m".
    /// Remembered per account so the chart opens on the user's last choice.
    /// </summary>
    public string PortfolioChartRange { get; set; } = PortfolioChartRanges.AllTime;

    /// <summary>
    /// UTC timestamp of the user's most recent successful login. Null until they
    /// log in for the first time. Stamped by the login handler and used by the
    /// admin activity dashboard to bucket users by how recently they signed in.
    /// </summary>
    public DateTime? LastLoginAt { get; set; }

    /// <summary>
    /// UTC timestamp of the user's most recent authenticated request, updated by
    /// <c>LastSeenMiddleware</c> on a throttled basis (at most once every few
    /// minutes per user). Unlike <see cref="LastLoginAt"/> this reflects ongoing
    /// activity for already-signed-in users, not just fresh logins, and drives the
    /// activity buckets on the admin dashboard.
    /// </summary>
    public DateTime? LastSeenAt { get; set; }
}