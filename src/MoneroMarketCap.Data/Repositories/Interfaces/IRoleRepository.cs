using MoneroMarketCap.Data.Models;

namespace MoneroMarketCap.Data.Repositories;

public interface IRoleRepository
{
    Task<IReadOnlyList<Role>> GetAllAsync();
    Task<Role?> GetByNameAsync(string name);
    Task<IReadOnlyList<UserRole>> GetUserRolesAsync(int userId);
    Task AssignRoleAsync(int userId, string roleName);
    Task RemoveRoleAsync(int userId, string roleName);
    Task<bool> UserHasRoleAsync(int userId, string roleName);
    Task EnsureRolesSeededAsync();
    Task SaveChangesAsync();
}