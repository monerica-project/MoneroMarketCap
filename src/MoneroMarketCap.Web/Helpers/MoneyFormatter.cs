using MoneroMarketCap.Services.Models;
using System.Globalization;

namespace MoneroMarketCap.Web.Helpers;

/// <summary>
/// Formats a USD-denominated decimal in a chosen display currency, applying
/// the right symbol, locale, and decimal style. Pair with <c>FiatRateService</c>
/// to get the conversion rate.
/// </summary>
public static class MoneyFormatter
{
    /// <summary>
    /// Converts <paramref name="valueUsd"/> from USD into <paramref name="currency"/>
    /// using <paramref name="ratePerUsd"/> (units of currency per 1 USD).
    /// </summary>
    public static decimal Convert(decimal valueUsd, decimal ratePerUsd) => valueUsd * ratePerUsd;

    /// <summary>
    /// Formats a price (typically per-coin) in the given currency. Uses extra decimal
    /// precision for sub-unit values so micro-cap coins still render readably.
    /// </summary>
    public static string FormatPrice(decimal valueUsd, CurrencyInfo currency, decimal ratePerUsd)
    {
        var v = Convert(valueUsd, ratePerUsd);
        var culture = SafeCulture(currency.Culture);

        string numberPart;
        if (currency.Decimals == 0)
        {
            numberPart = v >= 1
                ? v.ToString("N0", culture)
                : v.ToString("N2", culture);
        }
        else if (v >= 1)
        {
            numberPart = v.ToString($"N{currency.Decimals}", culture);
        }
        else if (v >= 0.01m)
        {
            numberPart = v.ToString("N4", culture);
        }
        else if (v >= 0.0001m)
        {
            numberPart = v.ToString("N6", culture);
        }
        else
        {
            numberPart = v.ToString("N8", culture);
        }

        return Wrap(numberPart, currency);
    }

    /// <summary>
    /// Formats a large value (market cap, volume) with K/M/B/T suffix in the chosen currency.
    /// </summary>
    public static string FormatLarge(decimal valueUsd, CurrencyInfo currency, decimal ratePerUsd)
    {
        var v = Convert(valueUsd, ratePerUsd);
        var culture = SafeCulture(currency.Culture);

        string numberPart = v switch
        {
            >= 1_000_000_000_000m => (v / 1_000_000_000_000m).ToString("N2", culture) + "T",
            >= 1_000_000_000m     => (v / 1_000_000_000m).ToString("N2", culture) + "B",
            >= 1_000_000m         => (v / 1_000_000m).ToString("N2", culture) + "M",
            >= 1_000m             => (v / 1_000m).ToString("N2", culture) + "K",
            _                     => v.ToString("N2", culture),
        };

        return Wrap(numberPart, currency);
    }

    private static string Wrap(string numberPart, CurrencyInfo currency) =>
        currency.SymbolBefore
            ? currency.Symbol + numberPart
            : numberPart + " " + currency.Symbol;

    private static CultureInfo SafeCulture(string name)
    {
        try { return CultureInfo.GetCultureInfo(name); }
        catch { return CultureInfo.InvariantCulture; }
    }
}
