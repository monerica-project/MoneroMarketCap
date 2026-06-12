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
}