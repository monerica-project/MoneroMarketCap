using System.Globalization;
using MoneroMarketCap.Data.Models;

namespace MoneroMarketCap.Data.Helpers;

/// <summary>
/// "Wallet difference": the user types what their wallet really holds and we work out the one
/// buy or sell that brings the tracked balance to that figure. More in the wallet than tracked
/// is a Buy of the gap; less is a Sell of the gap; equal needs nothing.
/// </summary>
public static class WalletDifference
{
    /// <summary>Largest balance accepted (guards against a pasted garbage number).</summary>
    public const decimal MaxBalance = 1_000_000_000_000m;

    /// <summary>The transaction needed, or null when the balances already match.</summary>
    public static (TransactionType Type, decimal Amount)? Compute(decimal trackedBalance, decimal walletBalance)
    {
        var gap = walletBalance - trackedBalance;
        if (gap == 0m)
        {
            return null;
        }

        return gap > 0m ? (TransactionType.Buy, gap) : (TransactionType.Sell, -gap);
    }

    /// <summary>
    /// Reads a typed balance such as "12.5", "1,234.56" or " 0.004 ". Always uses a dot as the
    /// decimal point (commas are thousands separators), whatever the server's culture.
    /// </summary>
    public static bool TryParseBalance(string? text, out decimal balance)
    {
        balance = 0m;
        var cleaned = (text ?? string.Empty).Trim().Replace(",", string.Empty).Replace(" ", string.Empty);
        return cleaned.Length > 0
            && decimal.TryParse(cleaned, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out balance)
            && balance >= 0m
            && balance <= MaxBalance;
    }
}
