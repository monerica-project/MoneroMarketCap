namespace MoneroMarketCap.Data.Models;

public class Portfolio : AuditableEntity
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public string Name { get; set; } = "My Portfolio";

    public ICollection<PortfolioCoin> PortfolioCoins { get; set; } = new List<PortfolioCoin>();

    public decimal TotalValueUsd => PortfolioCoins.Sum(pc => pc.TotalValueUsd);
}