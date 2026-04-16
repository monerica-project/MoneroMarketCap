using MoneroMarketCap.Data.Models;

namespace MoneroMarketCap.Data.Repositories;

public interface ICoinRepository
{
    Task<Coin?> GetByIdAsync(int id);
    Task<Coin?> GetBySymbolAsync(string symbol);
    Task<Coin?> GetByCoinGeckoIdAsync(string coinGeckoId);
    Task<IReadOnlyList<Coin>> GetAllAsync();
    Task AddAsync(Coin entity);
    Task RecordPriceSnapshotAsync(int coinId, string interval);
    Task<IReadOnlyList<CoinPriceHistory>> GetPriceHistoryAsync(int coinId, string interval, DateTime from, DateTime to);
    Task SaveChangesAsync();
}