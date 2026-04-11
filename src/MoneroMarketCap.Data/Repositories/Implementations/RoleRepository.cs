using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data.Constants;
using MoneroMarketCap.Data.Models;

namespace MoneroMarketCap.Data.Repositories;

public class RoleRepository : IRoleRepository
{
    private readonly AppDbContext _db;

    public RoleRepository(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<Role>> GetAllAsync() =>
        await _db.Roles.ToListAsync();

    public async Task<Role?> GetByNameAsync(string name) =>
        await _db.Roles.FirstOrDefaultAsync(r => r.Name == name);

    public async Task<IReadOnlyList<UserRole>> GetUserRolesAsync(int userId) =>
        await _db.UserRoles
            .Include(ur => ur.Role)
            .Where(ur => ur.UserId == userId)
            .ToListAsync();

    public async Task AssignRoleAsync(int userId, string roleName)
    {
        var role = await GetByNameAsync(roleName)
            ?? throw new Exception($"Role '{roleName}' not found.");

        var already = await _db.UserRoles
            .AnyAsync(ur => ur.UserId == userId && ur.RoleId == role.Id);

        if (already) return;

        _db.UserRoles.Add(new UserRole { UserId = userId, RoleId = role.Id });
        await _db.SaveChangesAsync();
    }

    public async Task RemoveRoleAsync(int userId, string roleName)
    {
        var role = await GetByNameAsync(roleName);
        if (role == null) return;

        var entry = await _db.UserRoles
            .FirstOrDefaultAsync(ur => ur.UserId == userId && ur.RoleId == role.Id);

        if (entry != null)
        {
            _db.UserRoles.Remove(entry);
            await _db.SaveChangesAsync();
        }
    }

    public async Task<bool> UserHasRoleAsync(int userId, string roleName) =>
        await _db.UserRoles
            .Include(ur => ur.Role)
            .AnyAsync(ur => ur.UserId == userId && ur.Role.Name == roleName);

    public async Task EnsureRolesSeededAsync()
    {
        foreach (var name in RoleNames.All)
        {
            if (!await _db.Roles.AnyAsync(r => r.Name == name))
                _db.Roles.Add(new Role { Name = name, Description = $"{name} role" });
        }
        await _db.SaveChangesAsync();
    }

    public async Task SaveChangesAsync() => await _db.SaveChangesAsync();
}