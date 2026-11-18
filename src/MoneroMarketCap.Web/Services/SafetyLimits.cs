namespace MoneroMarketCap.Web.Services;

/// <summary>
/// Limits added after the 2026-11-16 scan, when a script created 18 accounts and 45
/// transactions with nothing slowing it down. Only counts and times are held in memory.
/// </summary>
public static class SafetyLimits
{
    // Normal traffic is about one new account a week. A script made 38 in five hours on
    // 2026-11-16 with the old ceiling of 15 an hour, so the ceilings are now far tighter.
    public const int SignupsPerHour = 3;
    public const int SignupsPerDay = 8;

    /// <summary>
    /// A portfolio name as plain text: control characters (line breaks included) and the markup
    /// characters &lt; &gt; " ` are dropped, so a name can never carry a header line, a tag or
    /// a script fragment into a page, a file name or an export.
    /// </summary>
    public static string CleanName(string? value, int max)
    {
        var cleaned = new string((value ?? string.Empty)
            .Where(c => !char.IsControl(c) && c is not ('<' or '>' or '"' or '`' or '\\'))
            .ToArray()).Trim();
        return cleaned.Length > max ? cleaned[..max] : cleaned;
    }
    public const int FailedSignInsPer10Min = 40;
    public const int FailedSignInsPerNamePer10Min = 8;
    public const int MaxTransactionsPerPortfolioCoin = 5000;
    public const int MaxNotesLength = 500;
    public const int MaxExternalTransactionIdLength = 128;
    public const int MaxPortfolioNameLength = 100;

    public static string TransactionLimitMessage =>
        $"This coin already has the maximum of {MaxTransactionsPerPortfolioCoin:N0} transactions in this portfolio.";

    /// <summary>Cuts text to at most <paramref name="max"/> characters (null stays null).</summary>
    public static string? Clip(string? value, int max) =>
        value is not null && value.Length > max ? value[..max] : value;

    /// <summary>Bitcoin's first block: no transaction of any coin is older than this.</summary>
    public static readonly DateTime EarliestTransaction = new(2009, 1, 3, 0, 0, 0, DateTimeKind.Utc);

    private static readonly object Sync = new();
    private static readonly Dictionary<string, Queue<DateTime>> Marks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Keeps a transaction date inside a sensible range. A date in year 1 used to make the
    /// portfolio chart walk hundreds of thousands of days on every page view.
    /// </summary>
    public static DateTime ClampTransactionDate(DateTime value)
    {
        var utc = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        var latest = DateTime.UtcNow.AddDays(1);
        return utc < EarliestTransaction ? EarliestTransaction : utc > latest ? latest : utc;
    }

    public static bool Reached(string key, int limit, TimeSpan window)
    {
        lock (Sync)
        {
            return Window(key, window).Count >= limit;
        }
    }

    public static void Mark(string key)
    {
        lock (Sync)
        {
            if (Marks.Count > 5000)
            {
                foreach (var k in Marks.Where(p => p.Value.Count == 0).Select(p => p.Key).ToList())
                {
                    Marks.Remove(k);
                }
            }

            Window(key, TimeSpan.FromHours(1)).Enqueue(DateTime.UtcNow);
        }
    }

    private static Queue<DateTime> Window(string key, TimeSpan window)
    {
        if (!Marks.TryGetValue(key, out var q))
        {
            q = new Queue<DateTime>();
            Marks[key] = q;
        }

        var now = DateTime.UtcNow;
        while (q.Count > 0 && now - q.Peek() >= window)
        {
            q.Dequeue();
        }

        return q;
    }
}
