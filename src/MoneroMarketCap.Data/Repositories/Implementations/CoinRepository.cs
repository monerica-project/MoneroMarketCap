using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data.Models;

namespace MoneroMarketCap.Data.Repositories;

public class CoinRepository : ICoinRepository
{
    private readonly AppDbContext _db;
    public CoinRepository(AppDbContext db) => _db = db;

    public async Task<Coin?> GetByIdAsync(int id) =>
        await _db.Coins.FindAsync(id);

    public async Task<Coin?> GetBySymbolAsync(string symbol) =>
        await _db.Coins.FirstOrDefaultAsync(c => c.Symbol == symbol);

    public async Task<IReadOnlyList<Coin>> GetAllAsync() =>
        await _db.Coins.ToListAsync();

    public async Task AddAsync(Coin entity) => await _db.Coins.AddAsync(entity);

    public async Task RecordPriceSnapshotAsync(int coinId, string interval)
    {
        var coin = await _db.Coins.FindAsync(coinId);
        if (coin == null) return;

        _db.CoinPriceHistory.Add(new CoinPriceHistory
        {
            CoinId = coinId,
            PriceUsd = coin.PriceUsd,
            CirculatingSupply = coin.CirculatingSupply,
            MarketCapUsd = coin.MarketCapUsd,
            Interval = interval,
            RecordedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<CoinPriceHistory>> GetPriceHistoryAsync(int coinId, string interval, DateTime from, DateTime to) =>
        await _db.CoinPriceHistory
            .Where(h => h.CoinId == coinId && h.Interval == interval && h.RecordedAt >= from && h.RecordedAt <= to)
            .OrderBy(h => h.RecordedAt)
            .ToListAsync();

    public async Task SaveChangesAsync() => await _db.SaveChangesAsync();
}