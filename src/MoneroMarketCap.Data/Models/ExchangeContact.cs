namespace MoneroMarketCap.Data.Models;

/// <summary>A public contact method for an exchange (email, Telegram, X, Matrix, …).</summary>
public class ExchangeContact
{
    public int Id { get; set; }

    public int ExchangeId { get; set; }
    public Exchange? Exchange { get; set; }

    /// <summary>Channel name (Email, X, Telegram, Signal, Matrix, …).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>The address / handle / URL.</summary>
    public string Value { get; set; } = string.Empty;

    public string? Label { get; set; }
}
