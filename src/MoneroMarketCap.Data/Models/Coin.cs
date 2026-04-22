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

    public ICollection<CoinPriceHistory> PriceHistory { get; set; } = new List<CoinPriceHistory>();
}