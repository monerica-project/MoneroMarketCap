// Worker/MoneroSupplyWorker.cs
using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data;
using MoneroMarketCap.Services.Interfaces;

namespace MoneroMarketCap.Worker;

public class MoneroSupplyWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MoneroSupplyWorker> _logger;
    private readonly IConfiguration _config;

    private const string MoneroCoinGeckoId = "monero";

    public MoneroSupplyWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MoneroSupplyWorker> logger,
        IConfiguration config)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MoneroSupplyWorker starting");

        var intervalMinutes = _config.GetValue<int>("Monerod:RefreshIntervalMinutes", 5);
        var interval = TimeSpan.FromMinutes(intervalMinutes);

        // Small startup delay so we don't race the CoinGecko backfill on cold boots.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Monero supply refresh failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var supplySvc = scope.ServiceProvider.GetRequiredService<IMoneroSupplyService>();

        var monero = await db.Coins
            .FirstOrDefaultAsync(c => c.CoinGeckoId == MoneroCoinGeckoId, ct);

        if (monero is null)
        {
            _logger.LogDebug("Monero row not yet present; skipping");
            return;
        }

        var (height, supplyXmr) = await supplySvc.GetHeightAndSupplyAsync(ct);

        monero.NodeSupplyHeight = height;
        monero.NodeSupply = supplyXmr;
        monero.NodeSupplyUpdatedAt = DateTime.UtcNow;

        // These are from the old 128-bit design and no longer used. Clear them
        // if they were previously populated, or leave them null. Either is fine.
        monero.NodeEmissionHigh64 = null;
        monero.NodeEmissionLow64 = null;

        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Monero supply updated. Height: {Height}, NodeSupply: {Supply} XMR",
            height, supplyXmr);
    }
}