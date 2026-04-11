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

        // Seed top 100 on first run if db is empty
        await SeedIfEmptyAsync();

        var intervalMinutes = _config.GetValue<int>("CoinGecko:RefreshIntervalMinutes", 5);
        var interval = TimeSpan.FromMinutes(intervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshPricesAsync();
            _logger.LogInformation("Next refresh in {Minutes} minutes", intervalMinutes);
            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task SeedIfEmptyAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var gecko = scope.ServiceProvider.GetRequiredService<ICoinGeckoService>();

        var hasCoins = await db.Coins.AnyAsync();
        if (hasCoins)
        {
            _logger.LogInformation("Coins already exist, skipping seed");
            return;
        }

        _logger.LogInformation("No coins found — seeding top {Count} coins from CoinGecko",
            _config.GetValue<int>("CoinGecko:TopCoinsOnStartup", 100));

        var count = _config.GetValue<int>("CoinGecko:TopCoinsOnStartup", 100);
        var topCoins = await gecko.GetTopCoinsAsync(count);

        foreach (var m in topCoins)
            db.Coins.Add(MapToCoin(m));

        await db.SaveChangesAsync();
        _logger.LogInformation("Seeded {Count} coins", topCoins.Count);
    }

    private async Task RefreshPricesAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var gecko = scope.ServiceProvider.GetRequiredService<ICoinGeckoService>();

        var coins = await db.Coins
            .Where(c => c.IsActive && c.CoinGeckoId != "")
            .ToListAsync();

        if (!coins.Any())
        {
            _logger.LogWarning("No active coins to refresh");
            return;
        }

        _logger.LogInformation("Refreshing prices for {Count} coins", coins.Count);

        // Batch in chunks of 250 (CoinGecko max per request)
        var chunks = coins.Chunk(250);

        foreach (var chunk in chunks)
        {
            var data = await gecko.GetMarketDataBatchAsync(chunk.Select(c => c.CoinGeckoId));

            foreach (var coin in chunk)
            {
                if (!data.TryGetValue(coin.CoinGeckoId, out var m)) continue;
                UpdateCoin(coin, m);
            }

            // Respect rate limit between chunks
            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        await db.SaveChangesAsync();
        _logger.LogInformation("Prices refreshed at {Time}", DateTime.UtcNow);
    }

    private static Coin MapToCoin(CoinGeckoMarketData m) => new ()
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
        IsActive = true
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
        coin.UpdatedAt = DateTime.UtcNow;
    }
}