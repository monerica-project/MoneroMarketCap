using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Repositories;
using MoneroMarketCap.Services.Implementations;
using MoneroMarketCap.Services.Interfaces;
using MoneroMarketCap.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddHttpClient<ICoinGeckoService, CoinGeckoService>();
builder.Services.AddScoped<ICoinRepository, CoinRepository>();

builder.Services.AddHostedService<CoinSyncWorker>();

var host = builder.Build();
host.Run();