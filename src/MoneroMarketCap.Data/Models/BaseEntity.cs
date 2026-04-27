namespace MoneroMarketCap.Data.Models;

/// <summary>
/// Common timestamps for any persisted entity.
/// CreatedAt is set once on insert; UpdatedAt is refreshed on every save.
/// Both are auto-managed by AppDbContext.SaveChangesAsync — do not set manually.
/// </summary>
public abstract class BaseEntity
{
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}