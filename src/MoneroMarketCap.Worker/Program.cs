using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Repositories;
using MoneroMarketCap.Services.Implementations;
using MoneroMarketCap.Services.Interfaces;
using MoneroMarketCap.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddHttpClient<ICoinGeckoService, CoinGeckoService>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectTimeout = TimeSpan.FromSeconds(15),
        EnableMultipleHttp2Connections = false,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Tcp);
            socket.NoDelay = true;
            await socket.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, cancellationToken);
            return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
        }
    });

builder.Services.AddScoped<ICoinRepository, CoinRepository>();

// Runs once on startup to fill any gap in daily history for active coins.
builder.Services.AddSingleton<CoinHistoryBackfillService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CoinHistoryBackfillService>());

// Ongoing: reconciles top N, upserts coin rows, upserts today's history row each cycle.
builder.Services.AddHostedService<CoinPriceUpdateService>();

var host = builder.Build();
host.Run();
