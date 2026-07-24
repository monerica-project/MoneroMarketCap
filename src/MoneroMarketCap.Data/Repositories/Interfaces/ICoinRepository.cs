using MoneroMarketCap.Data.Models;

namespace MoneroMarketCap.Data.Repositories;

public interface ICoinRepository
{
    Task<Coin?> GetByIdAsync(int id);
    Task<Coin?> GetBySymbolAsync(string symbol);
    Task<Coin?> GetByCoinGeckoIdAsync(string coinGeckoId);
    Task<IReadOnlyList<Coin>> GetAllAsync();

    /// <summary>
    /// Active coins plus "grace" coins — ones that have dropped out of the top N within
    /// the last <paramref name="graceDays"/> days. Used to resolve coin pages so a recent
    /// dropout keeps a working URL instead of 404ing; such pages are marked noindex and
    /// excluded from the sitemap.
    /// </summary>
    Task<IReadOnlyList<Coin>> GetActiveAndGraceAsync(int graceDays);

    /// <summary>Number of active coins whose market data is older than the given cutoff.</summary>
    Task<int> CountStaleActiveAsync(DateTime olderThanUtc);
    Task AddAsync(Coin entity);
    Task RecordPriceSnapshotAsync(int coinId, string interval);
    Task<IReadOnlyList<CoinPriceHistory>> GetPriceHistoryAsync(int coinId, string interval, DateTime from, DateTime to);
    Task SaveChangesAsync();
}