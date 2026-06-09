using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MoneroMarketCap.Data;
using MoneroMarketCap.Services.Interfaces;

namespace MoneroMarketCap.Worker;

/// <summary>
/// One-shot backfill (--changenow-backfill). Resolves a ChangeNOW "from" ticker for every
/// coin without one yet (existing coins the migration stamped, plus any new coin a refresh
/// missed). Runs in the Worker — which forces IPv4 — so it actually reaches the API. Exits when done.
/// </summary>
public static class ChangeNowBackfill
{
    public static async Task RunAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var changeNow = sp.GetRequiredService<IChangeNowLinkService>();
        var db = sp.GetRequiredService<AppDbContext>();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("ChangeNowBackfill");

        if (!changeNow.Enabled)
        {
            logger.LogWarning("ChangeNOW disabled (no template) — backfill aborted, nothing changed.");
            return;
        }

        await changeNow.RefreshAsync();
        if (!changeNow.IsWarm)
        {
            logger.LogError("Could not load the ChangeNOW currency list — backfill aborted, nothing changed.");
            Console.WriteLine("ChangeNOW backfill: FAILED to load currency list (check connectivity). Nothing changed.");
            return;
        }

        var coins = await db.Coins
            .Where(c => c.ChangeNowTicker == null || c.ChangeNowTicker == "")
            .ToListAsync();

        var now = DateTime.UtcNow;
        var resolved = 0;
        foreach (var coin in coins)
        {
            var from = changeNow.ResolveFromTicker(coin.Symbol);
            coin.ChangeNowTicker = from;
            coin.ChangeNowCheckedAt = now;
            if (from != null) resolved++;
        }

        await db.SaveChangesAsync();

        logger.LogInformation(
            "ChangeNOW backfill complete: {Resolved}/{Total} link-less coins now have a ticker.",
            resolved, coins.Count);
        Console.WriteLine($"ChangeNOW backfill: {resolved}/{coins.Count} resolved.");
    }
}