using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MoneroMarketCap.Data.Models;

/// <summary>
/// A single fiat conversion rate, denominated as units of <see cref="Code"/> per 1 USD,
/// captured on a specific <see cref="Date"/>. One row per (Code, Date) pair.
///
/// Sourced from frankfurter.app (European Central Bank reference rates), which publishes
/// rates on weekdays only. Weekend/holiday rows are forward-filled from the most recent
/// preceding weekday at insert time so chart lookups can do a simple equality join.
///
/// USD itself is never stored; consumers should treat USD as RatePerUsd = 1 on every date.
/// </summary>
public class FiatRateHistory : AuditableEntity
{
    public int Id { get; set; }

    /// <summary>ISO 4217 code, uppercase (e.g. "EUR", "JPY", "ARS").</summary>
    [Required]
    [MaxLength(3)]
    [Column(TypeName = "varchar(3)")]
    public string Code { get; set; } = string.Empty;

    /// <summary>UTC date this rate applies to (always 00:00:00 UTC).</summary>
    public DateTime Date { get; set; }

    /// <summary>How many units of this currency equal 1 USD on this date.</summary>
    public decimal RatePerUsd { get; set; }
}
