namespace MoneroMarketCap.Services.Models;

/// <summary>
/// Display metadata for a supported display currency. The actual conversion rate
/// is held separately in <c>FiatRate</c> (the DB table).
/// </summary>
public sealed record CurrencyInfo(
    string Code,
    string Name,
    string Symbol,
    bool SymbolBefore,
    int Decimals,
    string Culture);

/// <summary>
/// Central catalog of currencies that the UI can display. To add a new currency:
///   1) Append a row here.
///   2) Make sure open.er-api.com returns it (most ISO 4217 codes are covered).
///   3) No DB migration needed; the row appears after the next FX sync cycle.
/// </summary>
public static class CurrencyCatalog
{
    public const string DefaultCode = "USD";

    public static readonly IReadOnlyList<CurrencyInfo> All = new[]
    {
        new CurrencyInfo("USD", "US Dollar",         "$",  true,  2, "en-US"),
        new CurrencyInfo("EUR", "Euro",              "€",  true,  2, "en-IE"),
        new CurrencyInfo("GBP", "British Pound",     "£",  true,  2, "en-GB"),
        new CurrencyInfo("JPY", "Japanese Yen",      "¥",  true,  0, "ja-JP"),
        new CurrencyInfo("CNY", "Chinese Yuan",      "¥",  true,  2, "zh-CN"),
        new CurrencyInfo("RUB", "Russian Ruble",     "₽",  false, 2, "ru-RU"),
        new CurrencyInfo("ARS", "Argentine Peso",    "$",  true,  2, "es-AR"),
        new CurrencyInfo("BRL", "Brazilian Real",    "R$", true,  2, "pt-BR"),
        new CurrencyInfo("INR", "Indian Rupee",      "₹",  true,  2, "en-IN"),
        new CurrencyInfo("CAD", "Canadian Dollar",   "$",  true,  2, "en-CA"),
        new CurrencyInfo("AUD", "Australian Dollar", "$",  true,  2, "en-AU"),
        new CurrencyInfo("CHF", "Swiss Franc",       "Fr", true,  2, "de-CH"),
        new CurrencyInfo("KRW", "South Korean Won",  "₩",  true,  0, "ko-KR"),
        new CurrencyInfo("MXN", "Mexican Peso",      "$",  true,  2, "es-MX"),
        new CurrencyInfo("TRY", "Turkish Lira",      "₺",  true,  2, "tr-TR"),
        new CurrencyInfo("ZAR", "South African Rand","R",  true,  2, "en-ZA"),
    };

    private static readonly Dictionary<string, CurrencyInfo> _byCode =
        All.ToDictionary(c => c.Code, c => c, StringComparer.OrdinalIgnoreCase);

    public static CurrencyInfo Default => _byCode[DefaultCode];

    public static bool IsSupported(string? code) =>
        !string.IsNullOrEmpty(code) && _byCode.ContainsKey(code);

    public static CurrencyInfo Get(string? code) =>
        code != null && _byCode.TryGetValue(code, out var c) ? c : Default;

    public static IEnumerable<string> SupportedCodes => _byCode.Keys;
}
