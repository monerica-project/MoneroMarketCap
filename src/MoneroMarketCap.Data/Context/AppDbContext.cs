using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data.Models;

namespace MoneroMarketCap.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<AppUser> Users { get; set; }
    public DbSet<Role> Roles { get; set; }
    public DbSet<UserRole> UserRoles { get; set; }
    public DbSet<Coin> Coins { get; set; }
    public DbSet<CoinPriceHistory> CoinPriceHistory { get; set; }
    public DbSet<Portfolio> Portfolios { get; set; }
    public DbSet<PortfolioCoin> PortfolioCoins { get; set; }
    public DbSet<CoinTransaction> CoinTransactions { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserRole>()
            .HasKey(ur => new { ur.UserId, ur.RoleId });

        modelBuilder.Entity<UserRole>()
            .HasOne(ur => ur.User)
            .WithMany(u => u.UserRoles)
            .HasForeignKey(ur => ur.UserId);

        modelBuilder.Entity<UserRole>()
            .HasOne(ur => ur.Role)
            .WithMany(r => r.UserRoles)
            .HasForeignKey(ur => ur.RoleId);

        modelBuilder.Entity<PortfolioCoin>()
            .HasIndex(pc => new { pc.PortfolioId, pc.CoinId })
            .IsUnique();

        modelBuilder.Entity<CoinTransaction>()
            .Property(t => t.Type)
            .HasConversion<string>();
    }
}