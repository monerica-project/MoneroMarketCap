using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data;

namespace MoneroMarketCap.Web.Services;

/// <summary>
/// Account-wide portfolio limits. The per-coin cap in <see cref="SafetyLimits"/> alone let one
/// account spread transactions over hundreds of coins and many portfolios, so a single member
/// could still fill the database. These caps sit far above what a real person tracks.
/// </summary>
public static class PortfolioLimits
{
    public const int MaxTransactionsPerUser = 20_000;
    public const int MaxCoinsPerPortfolio = 200;

    public static string UserTransactionLimitMessage =>
        $"Your account already has the maximum of {MaxTransactionsPerUser:N0} transactions across all portfolios. Delete some old ones to add more.";

    public static string CoinLimitMessage =>
        $"This portfolio already holds the maximum of {MaxCoinsPerPortfolio:N0} coins. Remove one or use another portfolio.";

    public const string UnknownCoinMessage = "That coin was not found. Please pick a coin from the list.";

    /// <summary>All transactions in every portfolio owned by this member.</summary>
    public static Task<int> CountUserTransactionsAsync(AppDbContext db, int userId) =>
        db.CoinTransactions.CountAsync(t => t.PortfolioCoin.Portfolio.UserId == userId);

    /// <summary>How many more transactions this member may add before reaching the account cap (never below 0).</summary>
    public static async Task<int> UserRoomLeftAsync(AppDbContext db, int userId) =>
        Math.Max(0, MaxTransactionsPerUser - await CountUserTransactionsAsync(db, userId));

    /// <summary>
    /// The coin id comes from a form field, so anyone can post a made-up number. Saving it
    /// would fail on the foreign key with an error page instead of a clear message.
    /// </summary>
    public static Task<bool> CoinExistsAsync(AppDbContext db, int coinId) =>
        coinId > 0 ? db.Coins.AnyAsync(c => c.Id == coinId) : Task.FromResult(false);

    /// <summary>True when the portfolio may take one more coin it does not hold yet.</summary>
    public static async Task<bool> CanAddCoinAsync(AppDbContext db, int portfolioId) =>
        await db.PortfolioCoins.CountAsync(pc => pc.PortfolioId == portfolioId) < MaxCoinsPerPortfolio;
}
