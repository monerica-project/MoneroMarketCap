using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MoneroMarketCap.Data;
using MoneroMarketCap.Services.Interfaces;

namespace MoneroMarketCap.Tests;

/// <summary>In-memory AppDbContext plumbing shared by the tests.</summary>
internal static class TestDb
{
    public static AppDbContext Create(string? name = null) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString())
            .Options);

    /// <summary>A service provider whose scopes resolve an AppDbContext on one shared in-memory DB.</summary>
    public static ServiceProvider Services(string dbName, ICoinGeckoService? gecko = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddMemoryCache();
        if (gecko != null)
        {
            services.AddSingleton(gecko);
        }

        return services.BuildServiceProvider();
    }

    public static IConfiguration Config(Dictionary<string, string?>? values = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(values ?? new()).Build();
}
