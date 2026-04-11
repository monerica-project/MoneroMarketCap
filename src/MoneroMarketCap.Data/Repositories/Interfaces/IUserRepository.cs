using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Data.Repositories.Interfaces;

namespace MoneroMarketCap.Data.Repositories;

public interface IUserRepository : IRepository<AppUser>
{
    Task<AppUser?> GetByUsernameAsync(string username);
}