﻿using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data.Models;

namespace MoneroMarketCap.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<AppUser> Users { get; set; }
    public DbSet<Role> Roles { get; set; }
    public DbSet<UserRole> UserRoles { get; set; }
    public DbSet<Coin> Coins { get; set; }
    public DbSet<Portfolio> Portfolios { get; set; }
    public DbSet<PortfolioCoin> PortfolioCoins { get; set; }
    public DbSet<CoinTransaction> CoinTransactions { get; set; }
    public DbSet<CoinPriceHistory> CoinPriceHistories { get; set; }
    public DbSet<FiatRate> FiatRates { get; set; }
    public DbSet<FiatRateHistory> FiatRateHistories { get; set; }
    public DbSet<CoinExchange> CoinExchanges { get; set; }

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

        modelBuilder.Entity<FiatRate>()
            .Property(f => f.RatePerUsd)
            .HasColumnType("numeric(20, 10)");

        modelBuilder.Entity<FiatRateHistory>(b =>
        {
            b.Property(f => f.RatePerUsd).HasColumnType("numeric(20, 10)");
            // One row per currency per day. Also doubles as the lookup index used by
            // the chart endpoint when joining CoinPriceHistories on Date + Code.
            b.HasIndex(f => new { f.Code, f.Date }).IsUnique();
            b.HasIndex(f => f.Date);
        });

        modelBuilder.Entity<CoinExchange>(b =>
        {
            // One row per coin+exchange; the SwapRaven profile URL is the stable key.
            b.HasIndex(e => new { e.CoinId, e.Url }).IsUnique();
            b.HasIndex(e => e.CoinId);
            b.Property(e => e.FeeMinPercent).HasColumnType("numeric(9, 4)");
            b.Property(e => e.FeeMaxPercent).HasColumnType("numeric(9, 4)");
            b.HasOne(e => e.Coin)
                .WithMany()
                .HasForeignKey(e => e.CoinId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        StampTimestamps();
        return base.SaveChanges();
    }

    /// <summary>
    /// Auto-stamps CreatedAt on insert and UpdatedAt on every save for any
    /// entity inheriting from AuditableEntity. CreatedAt is never modified after insert.
    /// </summary>
    private void StampTimestamps()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<AuditableEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    entry.Entity.UpdatedAt = now;
                    break;
                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    entry.Property(nameof(AuditableEntity.CreatedAt)).IsModified = false;
                    break;
            }
        }
    }
}