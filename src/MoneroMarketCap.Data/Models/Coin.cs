using System.ComponentModel.DataAnnotations.Schema;

namespace MoneroMarketCap.Data.Models;

public class Coin : AuditableEntity
{
    public int Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string CoinGeckoId { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }

    // Pricing
    public decimal PriceUsd { get; set; }
    public decimal PriceChangePercent24h { get; set; }
    public decimal High24h { get; set; }
    public decimal Low24h { get; set; }

    // Market data
    public decimal MarketCapUsd { get; set; }
    public int MarketCapRank { get; set; }
    public decimal FullyDilutedValuation { get; set; }
    public decimal TotalVolume { get; set; }

    // Supply
    public decimal CirculatingSupply { get; set; }
    public decimal TotalSupply { get; set; }
    public decimal? MaxSupply { get; set; }

    // ATH / ATL
    public decimal Ath { get; set; }
    public decimal AthChangePercentage { get; set; }
    public DateTime? AthDate { get; set; }
    public decimal Atl { get; set; }
    public decimal AtlChangePercentage { get; set; }
    public DateTime? AtlDate { get; set; }

    public bool IsActive { get; set; } = true;

    // Admin-managed affiliate link for trading this coin for Monero.
    // When set, a "Trade ... for Monero" button appears on the coin's detail page.
    public string? TradeUrl { get; set; }

    // Auto-resolved ChangeNOW "from" ticker (legacy ticker, e.g. "bnbbsc"), filled
    // lazily on first coin-page load when TradeUrl is empty and the asset is listed
    // on ChangeNOW. The effective link is built from this + the config template at
    // render time, so a link_id/template change propagates without rewriting rows.
    // Null = not yet resolved (or no match — cheap to re-check next load).
    public string? ChangeNowTicker { get; set; }

    // When the Worker last attempted ChangeNOW resolution for this coin. Null = never
    // attempted ("new coin"); the Worker resolves only these automatically and never
    // re-checks afterward. The migration stamps all pre-existing rows so they're treated
    // as known and handled only via the manual backfill.
    public DateTime? ChangeNowCheckedAt { get; set; }

    public decimal PriceChangePercent1h { get; set; }
    public decimal PriceChangePercent7d { get; set; }
    public decimal PriceChangePercent30d { get; set; }
    public decimal PriceChangePercent1y { get; set; }

    // --- Node-sourced supply (independent of CoinGecko columns) ---
    [Column("NodeSupply")]
    public decimal? NodeSupply { get; set; }

    [Column("NodeSupplyHeight")]
    public ulong? NodeSupplyHeight { get; set; }

    [Column("NodeSupplyUpdatedAt")]
    public DateTime? NodeSupplyUpdatedAt { get; set; }

    /// <summary>
    /// When the one-year daily ("1d") history backfill last completed for this coin.
    /// Null means it has never been backfilled — i.e. a new entrant that still needs
    /// its year of history. This is the guard that stops a coin younger than the
    /// backfill threshold from being re-fetched from CoinGecko on every cycle
    /// (it can never accumulate enough rows to satisfy a row-count test).
    /// </summary>
    [Column("DailyHistoryBackfilledAtUtc")]
    public DateTime? DailyHistoryBackfilledAtUtc { get; set; }

    /// <summary>
    /// Sanitized English description from CoinGecko's /coins/{id} endpoint (plain text,
    /// HTML stripped). Null = never fetched; empty = fetched but CoinGecko has none for
    /// this coin (e.g. Monero itself). Rendered in the "About" section of the coin page.
    /// </summary>
    [Column("Description")]
    public string? Description { get; set; }

    /// <summary>When <see cref="Description"/> was last fetched. Drives the slow refresh
    /// cadence and stops re-fetching descriptions CoinGecko simply doesn't have.</summary>
    [Column("DescriptionUpdatedAtUtc")]
    public DateTime? DescriptionUpdatedAtUtc { get; set; }

    /// <summary>
    /// Last time this coin appeared in the CoinGecko top N. Drives the tracking
    /// lifecycle: still in the list = Active; out but stamped within
    /// CoinGecko:TrackingGraceDays = Grace (kept, not indexed); older = Retired
    /// (no longer polled). Null for coins that predate this column.
    /// </summary>
    [Column("LastInTopNUtc")]
    public DateTime? LastInTopNUtc { get; set; }

    public ICollection<CoinPriceHistory> PriceHistory { get; set; } = new List<CoinPriceHistory>();
}