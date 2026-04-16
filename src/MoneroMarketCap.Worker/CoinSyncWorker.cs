using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Services.Interfaces;
using MoneroMarketCap.Services.Models;

namespace MoneroMarketCap.Worker;

public class CoinSyncWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CoinSyncWorker> _logger;
    private readonly IConfiguration _config;

    public CoinSyncWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<CoinSyncWorker> logger,
        IConfiguration config)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CoinSyncWorker starting");

        var intervalMinutes = _config.GetValue<int>("CoinGecko:RefreshIntervalMinutes", 5);
        var interval = TimeSpan.FromMinutes(intervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAndRefreshAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reconcile cycle failed");
            }

            _logger.LogInformation("Next refresh in {Minutes} minutes", intervalMinutes);
            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task ReconcileAndRefreshAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var gecko = scope.ServiceProvider.GetRequiredService<ICoinGeckoService>();

        var topCount = _config.GetValue<int>("CoinGecko:TopCount", 100);

        // 1. Live top N from CoinGecko — source of truth
        var topCoins = await gecko.GetTopCoinsAsync(topCount);
        if (topCoins == null || topCoins.Count == 0)
        {
            _logger.LogWarning("CoinGecko returned no top coins; skipping cycle");
            return;
        }

        var topIds = topCoins.Select(c => c.Id).ToHashSet();

        // 2. Load everything we already track
        var existing = await db.Coins.ToListAsync();
        var existingById = existing
            .Where(c => !string.IsNullOrEmpty(c.CoinGeckoId))
            .ToDictionary(c => c.CoinGeckoId, c => c);

        int added = 0, updated = 0, reactivated = 0, deactivated = 0;

        // 3. Upsert each coin currently in the top N
        foreach (var m in topCoins)
        {
            if (existingById.TryGetValue(m.Id, out var coin))
            {
                if (!coin.IsActive) reactivated++;
                UpdateCoin(coin, m);
                coin.IsActive = true;
                updated++;
            }
            else
            {
                var newCoin = MapToCoin(m);
                db.Coins.Add(newCoin);
                added++;
            }
        }

        // 4. Deactivate coins that dropped out of the top N
        foreach (var coin in existing)
        {
            if (string.IsNullOrEmpty(coin.CoinGeckoId)) continue;
            if (!topIds.Contains(coin.CoinGeckoId) && coin.IsActive)
            {
                coin.IsActive = false;
                deactivated++;
            }
        }

        await db.SaveChangesAsync();

        _logger.LogInformation(
            "Reconcile complete. Top: {Top}, Added: {Added}, Updated: {Updated}, Reactivated: {Reactivated}, Deactivated: {Deactivated}",
            topCount, added, updated, reactivated, deactivated);
    }

    private static Coin MapToCoin(CoinGeckoMarketData m) => new()
    {
        CoinGeckoId = m.Id,
        Symbol = m.Symbol.ToUpper(),
        Name = m.Name,
        ImageUrl = m.Image,
        PriceUsd = m.CurrentPrice ?? 0,
        PriceChangePercent24h = m.PriceChangePercentage24h ?? 0,
        High24h = m.High24h ?? 0,
        Low24h = m.Low24h ?? 0,
        MarketCapUsd = m.MarketCap ?? 0,
        MarketCapRank = m.MarketCapRank ?? 0,
        FullyDilutedValuation = m.FullyDilutedValuation ?? 0,
        TotalVolume = m.TotalVolume ?? 0,
        CirculatingSupply = m.CirculatingSupply ?? 0,
        TotalSupply = m.TotalSupply ?? 0,
        MaxSupply = m.MaxSupply,
        Ath = m.Ath ?? 0,
        AthChangePercentage = m.AthChangePercentage ?? 0,
        AthDate = m.AthDate,
        Atl = m.Atl ?? 0,
        AtlChangePercentage = m.AtlChangePercentage ?? 0,
        AtlDate = m.AtlDate,
        PriceChangePercent1h = m.PriceChangePercentage1h ?? 0,
        PriceChangePercent7d = m.PriceChangePercentage7d ?? 0,
        PriceChangePercent30d = m.PriceChangePercentage30d ?? 0,
        PriceChangePercent1y = m.PriceChangePercentage1y ?? 0,
        IsActive = true,
        UpdatedAt = DateTime.UtcNow,
    };

    private static void UpdateCoin(Coin coin, CoinGeckoMarketData m)
    {
        coin.PriceUsd = m.CurrentPrice ?? coin.PriceUsd;
        coin.PriceChangePercent24h = m.PriceChangePercentage24h ?? coin.PriceChangePercent24h;
        coin.High24h = m.High24h ?? coin.High24h;
        coin.Low24h = m.Low24h ?? coin.Low24h;
        coin.MarketCapUsd = m.MarketCap ?? coin.MarketCapUsd;
        coin.MarketCapRank = m.MarketCapRank ?? coin.MarketCapRank;
        coin.FullyDilutedValuation = m.FullyDilutedValuation ?? coin.FullyDilutedValuation;
        coin.TotalVolume = m.TotalVolume ?? coin.TotalVolume;
        coin.CirculatingSupply = m.CirculatingSupply ?? coin.CirculatingSupply;
        coin.TotalSupply = m.TotalSupply ?? coin.TotalSupply;
        coin.MaxSupply = m.MaxSupply ?? coin.MaxSupply;
        coin.ImageUrl = m.Image ?? coin.ImageUrl;
        coin.PriceChangePercent1h = m.PriceChangePercentage1h ?? coin.PriceChangePercent1h;
        coin.PriceChangePercent7d = m.PriceChangePercentage7d ?? coin.PriceChangePercent7d;
        coin.PriceChangePercent30d = m.PriceChangePercentage30d ?? coin.PriceChangePercent30d;
        coin.PriceChangePercent1y = m.PriceChangePercentage1y ?? coin.PriceChangePercent1y;
        coin.UpdatedAt = DateTime.UtcNow;
    }
}