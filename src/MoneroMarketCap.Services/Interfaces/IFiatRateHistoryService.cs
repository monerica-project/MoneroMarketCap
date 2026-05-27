namespace MoneroMarketCap.Services.Interfaces;

/// <summary>
/// Manages the FiatRateHistory table: historical USD-based FX rates for every
/// supported display currency, used to convert USD-denominated coin price
/// history into other currencies at chart-render time.
///
/// Data source: api.frankfurter.app (European Central Bank reference rates).
/// Free, no API key, no rate limit at this volume.
/// </summary>
public interface IFiatRateHistoryService
{
    /// <summary>
    /// Ensures FX rate history exists for every supported currency for each day
    /// in the past <paramref name="days"/> days. Idempotent — only inserts rows
    /// that don't already exist. Weekend/holiday gaps are forward-filled from
    /// the most recent preceding weekday's rate.
    /// </summary>
    /// <returns>Number of rows inserted (0 if everything was already in place).</returns>
    Task<int> BackfillAsync(int days, CancellationToken ct = default);

    /// <summary>
    /// Fetches the latest published rates from frankfurter and upserts them under
    /// today's UTC date. If frankfurter returns a date older than today (weekend),
    /// uses that older rate as today's value (forward-fill).
    /// </summary>
    /// <returns>Number of rows inserted or updated.</returns>
    Task<int> UpdateLatestAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns a date → rate map for a single currency over the given UTC date range.
    /// Used by the chart endpoint when rendering non-USD price history.
    /// USD always resolves to a constant 1.0 without touching the DB.
    /// </summary>
    Task<IReadOnlyDictionary<DateTime, decimal>> GetSeriesAsync(
        string code,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct = default);
}
