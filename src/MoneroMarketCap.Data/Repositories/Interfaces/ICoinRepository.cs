using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Data.Repositories.Interfaces;

namespace MoneroMarketCap.Data.Repositories;

public interface ICoinRepository : IRepository<Coin>
{
    Task<Coin?> GetBySymbolAsync(string symbol);
    Task RecordPriceSnapshotAsync(int coinId, string interval);
    Task<IReadOnlyList<CoinPriceHistory>> GetPriceHistoryAsync(int coinId, string interval, DateTime from, DateTime to);
}