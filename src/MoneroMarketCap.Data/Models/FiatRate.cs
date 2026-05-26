using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MoneroMarketCap.Data.Models;

/// <summary>
/// A single fiat conversion rate, denominated as units of <see cref="Code"/> per 1 USD.
/// USD itself is stored with <see cref="RatePerUsd"/> = 1.
/// </summary>
public class FiatRate : AuditableEntity
{
    /// <summary>ISO 4217 code, uppercase (e.g. "EUR", "JPY", "ARS").</summary>
    [Key]
    [MaxLength(3)]
    [Column(TypeName = "varchar(3)")]
    public string Code { get; set; } = string.Empty;

    /// <summary>How many units of this currency equal 1 USD.</summary>
    public decimal RatePerUsd { get; set; }
}
