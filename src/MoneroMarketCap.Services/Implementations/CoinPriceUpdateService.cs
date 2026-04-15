using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Services.Interfaces;

namespace MoneroMarketCap.Services.Implementations;

public class CoinPriceUpdateService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<CoinPriceUpdateService> logger;
    private readonly TimeSpan interval;

    public CoinPriceUpdateService(
        IServiceScopeFactory scopeFactory,
        ILogger<CoinPriceUpdateService> logger,
        IConfiguration config)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
        var minutes = config.GetValue<int>("CoinGecko:RefreshIntervalMinutes", 8);
        this.interval = TimeSpan.FromMinutes(minutes);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await UpdatePricesAsync();
            await Task.Delay(this.interval, stoppingToken);
        }
    }

    private async Task UpdatePricesAsync()
    {
        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var geckoService = scope.ServiceProvider.GetRequiredService<ICoinGeckoService>();

        var coins = await db.Coins
            .Where(c => c.IsActive && c.CoinGeckoId != "")
            .ToListAsync();

        if (!coins.Any())
            return;

        var data = await geckoService.GetMarketDataBatchAsync(coins.Select(c => c.CoinGeckoId));

        var today = DateTime.UtcNow.Date;

        foreach (var coin in coins)
        {
            if (!data.TryGetValue(coin.CoinGeckoId, out var m))
                continue;

            coin.PriceUsd = m.CurrentPrice ?? 0;
            coin.MarketCapUsd = m.MarketCap ?? 0;
            coin.CirculatingSupply = m.CirculatingSupply ?? 0;
            coin.PriceChangePercent24h = m.PriceChangePercentage24h ?? 0;
            coin.TotalVolume = m.TotalVolume ?? 0;
            coin.High24h = m.High24h ?? 0;
            coin.Low24h = m.Low24h ?? 0;
            coin.MarketCapRank = m.MarketCapRank ?? 0;
            coin.ImageUrl = m.Image ?? coin.ImageUrl;
            coin.PriceChangePercent1h = m.PriceChangePercentage1h ?? coin.PriceChangePercent1h;
            coin.PriceChangePercent7d = m.PriceChangePercentage7d ?? coin.PriceChangePercent7d;
            coin.PriceChangePercent30d = m.PriceChangePercentage30d ?? coin.PriceChangePercent30d;
            coin.PriceChangePercent1y = m.PriceChangePercentage1y ?? coin.PriceChangePercent1y;
            coin.UpdatedAt = DateTime.UtcNow;

            // Record one daily history entry per coin per day
            var alreadyRecorded = await db.CoinPriceHistories
                .AnyAsync(h => h.CoinId == coin.Id
                            && h.Interval == "1d"
                            && h.RecordedAt.Date == today);

            if (!alreadyRecorded)
            {
                db.CoinPriceHistories.Add(new CoinPriceHistory
                {
                    CoinId = coin.Id,
                    PriceUsd = coin.PriceUsd,
                    MarketCapUsd = coin.MarketCapUsd,
                    CirculatingSupply = coin.CirculatingSupply,
                    Interval = "1d",
                    RecordedAt = today
                });
            }
        }

        await db.SaveChangesAsync();

        this.logger.LogInformation(
            "Updated prices for {Count} coins at {Time} (interval: {Interval}min)",
            coins.Count, DateTime.UtcNow, this.interval.TotalMinutes);
    }
}