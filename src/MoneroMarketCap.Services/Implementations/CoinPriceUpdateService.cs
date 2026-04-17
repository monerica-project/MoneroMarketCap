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
    private readonly int topCount;

    public CoinPriceUpdateService(
        IServiceScopeFactory scopeFactory,
        ILogger<CoinPriceUpdateService> logger,
        IConfiguration config)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
        var minutes = config.GetValue<int>("CoinGecko:RefreshIntervalMinutes", 8);
        this.interval = TimeSpan.FromMinutes(minutes);
        this.topCount = config.GetValue<int>("CoinGecko:TopCount", 100);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await UpdatePricesAsync();
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Price update cycle failed");
            }
            await Task.Delay(this.interval, stoppingToken);
        }
    }

    private async Task UpdatePricesAsync()
    {
        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var geckoService = scope.ServiceProvider.GetRequiredService<ICoinGeckoService>();

        // 1. Pull the CURRENT top N from CoinGecko (source of truth)
        var topCoins = await geckoService.GetTopCoinsAsync(this.topCount);
        if (topCoins == null || topCoins.Count == 0)
        {
            this.logger.LogWarning("CoinGecko returned no top coins; skipping this cycle");
            return;
        }

        var topIds = topCoins.Select(c => c.Id).ToHashSet();

        // 2. Load all coins we already track
        var existing = await db.Coins.ToListAsync();
        var existingById = existing
            .Where(c => !string.IsNullOrEmpty(c.CoinGeckoId))
            .ToDictionary(c => c.CoinGeckoId, c => c);

        var today = DateTime.UtcNow.Date;
        int added = 0, updated = 0, deactivated = 0;

        // 3. Upsert every coin currently in the top N
        foreach (var m in topCoins)
        {
            if (!existingById.TryGetValue(m.Id, out var coin))
            {
                coin = new Coin
                {
                    CoinGeckoId = m.Id,
                    Symbol = m.Symbol?.ToUpper() ?? "",
                    Name = m.Name ?? "",
                    CreatedAt = DateTime.UtcNow
                };
                db.Coins.Add(coin);
                added++;
            }
            else
            {
                updated++;
            }

            // Price / market / volume fields that always refresh
            coin.IsActive = true;
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

            // ATH / ATL — must refresh every cycle. AthChangePercentage drifts every
            // tick, and Ath/AthDate jump whenever a new high is hit (e.g. RaveDAO).
            coin.Ath = m.Ath ?? coin.Ath;
            coin.AthChangePercentage = m.AthChangePercentage ?? coin.AthChangePercentage;
            coin.AthDate = m.AthDate ?? coin.AthDate;
            coin.Atl = m.Atl ?? coin.Atl;
            coin.AtlChangePercentage = m.AtlChangePercentage ?? coin.AtlChangePercentage;
            coin.AtlDate = m.AtlDate ?? coin.AtlDate;

            // Supply / valuation fields (issuance, burns, cap changes)
            coin.TotalSupply = m.TotalSupply ?? coin.TotalSupply;
            coin.MaxSupply = m.MaxSupply ?? coin.MaxSupply;
            coin.FullyDilutedValuation = m.FullyDilutedValuation ?? coin.FullyDilutedValuation;

            coin.UpdatedAt = DateTime.UtcNow;
        }

        // Save here so newly-added coins get Ids before we write history rows
        await db.SaveChangesAsync();

        // 4. Deactivate coins that fell out of the top N
        foreach (var coin in existing)
        {
            if (string.IsNullOrEmpty(coin.CoinGeckoId)) continue;
            if (!topIds.Contains(coin.CoinGeckoId) && coin.IsActive)
            {
                coin.IsActive = false;
                deactivated++;
            }
        }

        // 5. Upsert today's daily history entry for every active coin so the
        //    current-day point on the 1y chart always reflects the latest price.
        var activeCoins = db.Coins.Local
            .Where(c => c.IsActive && topIds.Contains(c.CoinGeckoId))
            .ToList();
        var activeIds = activeCoins.Select(c => c.Id).ToList();

        var todaysRows = await db.CoinPriceHistories
            .Where(h => h.Interval == "1d"
                     && h.RecordedAt == today
                     && activeIds.Contains(h.CoinId))
            .ToListAsync();

        var todaysByCoinId = todaysRows.ToDictionary(h => h.CoinId, h => h);

        int historyAdded = 0, historyUpdated = 0;

        foreach (var coin in activeCoins)
        {
            if (todaysByCoinId.TryGetValue(coin.Id, out var row))
            {
                row.PriceUsd = coin.PriceUsd;
                row.MarketCapUsd = coin.MarketCapUsd;
                row.CirculatingSupply = coin.CirculatingSupply;
                historyUpdated++;
            }
            else
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
                historyAdded++;
            }
        }

        await db.SaveChangesAsync();

        this.logger.LogInformation(
            "Top {Top} refresh complete. Coins added: {Added}, updated: {Updated}, deactivated: {Deactivated}. History added: {HAdded}, updated: {HUpdated}. Interval: {Interval}min",
            this.topCount, added, updated, deactivated, historyAdded, historyUpdated, this.interval.TotalMinutes);
    }
}