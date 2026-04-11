using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data.Models;

namespace MoneroMarketCap.Data.Repositories;

public class UserRepository : IUserRepository
{
    private readonly AppDbContext _db;
    public UserRepository(AppDbContext db) => _db = db;
  
    public async Task<IReadOnlyList<AppUser>> GetAllAsync() =>
        await _db.Users.ToListAsync();

    public async Task AddAsync(AppUser entity) => await _db.Users.AddAsync(entity);

    public async Task SaveChangesAsync() => await _db.SaveChangesAsync();

    public async Task<AppUser?> GetByIdAsync(int id) =>
    await _db.Users
        .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
        .Include(u => u.Portfolios)
        .FirstOrDefaultAsync(u => u.Id == id);

    public async Task<AppUser?> GetByUsernameAsync(string username) =>
        await _db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Username == username);
}