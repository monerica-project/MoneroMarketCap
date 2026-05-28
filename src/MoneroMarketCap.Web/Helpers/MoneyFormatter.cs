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
    /// Formats a money amount (portfolio value, total, cost basis) in the chosen currency,
    /// always using the currency's fixed decimal count (2 for USD, 0 for JPY/KRW). Unlike
    /// <see cref="FormatPrice"/> this never adds sub-unit precision, so a $0.47 total renders
    /// as "$0.47" rather than "$0.4659". Use for dollar amounts that are NOT per-coin prices.
    /// </summary>
    public static string FormatValue(decimal valueUsd, CurrencyInfo currency, decimal ratePerUsd)
    {
        var v = Convert(valueUsd, ratePerUsd);
        var culture = SafeCulture(currency.Culture);
        var numberPart = v.ToString($"N{currency.Decimals}", culture);
        return Wrap(numberPart, currency);
    }

    /// <summary>
    /// Formats a signed money amount (P&amp;L) in the chosen currency. Like <see cref="FormatPrice"/>
    /// it keeps sub-unit precision for small magnitudes so a tiny gain/loss isn't rounded to zero,
    /// but it applies the precision tiers to the magnitude and re-attaches the sign. This avoids
    /// the bug where a plain price formatter forces 8 decimals onto any negative value
    /// (e.g. a -$50 loss rendering as "-50.00000000").
    /// </summary>
    public static string FormatSigned(decimal valueUsd, CurrencyInfo currency, decimal ratePerUsd)
    {
        var v = Convert(valueUsd, ratePerUsd);
        var culture = SafeCulture(currency.Culture);
        var sign = v < 0 ? "-" : "";
        var a = Math.Abs(v);

        string numberPart;
        if (currency.Decimals == 0)
        {
            numberPart = a >= 1m ? a.ToString("N0", culture) : a.ToString("N2", culture);
        }
        else if (a >= 1m)
        {
            numberPart = a.ToString($"N{currency.Decimals}", culture);
        }
        else if (a >= 0.01m)
        {
            numberPart = a.ToString("N4", culture);
        }
        else if (a >= 0.0001m)
        {
            numberPart = a.ToString("N6", culture);
        }
        else
        {
            numberPart = a.ToString("N8", culture);
        }

        return sign + Wrap(numberPart, currency);
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