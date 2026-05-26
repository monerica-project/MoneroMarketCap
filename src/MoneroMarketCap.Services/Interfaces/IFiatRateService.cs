namespace MoneroMarketCap.Services.Interfaces;

public interface IFiatRateService
{
    /// <summary>
    /// Returns a USD-keyed lookup of rates per 1 USD (e.g. EUR => 0.92, JPY => 156.4).
    /// USD itself is present with rate 1.0. Reads cached rows from the DB; if the
    /// table is empty (first deploy before the worker has synced) returns just USD=1.
    /// </summary>
    Task<IReadOnlyDictionary<string, decimal>> GetRatesAsync(CancellationToken ct = default);

    /// <summary>
    /// Fetches fresh USD-based rates from the upstream FX provider and upserts the
    /// FiatRates table. Safe to call repeatedly; idempotent. Returns the number of
    /// currencies written (or 0 on failure).
    /// </summary>
    Task<int> RefreshAsync(CancellationToken ct = default);
}
