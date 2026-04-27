using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Repositories;
using MoneroMarketCap.Services.Implementations;
using MoneroMarketCap.Services.Interfaces;
using MoneroMarketCap.Worker;
using Microsoft.EntityFrameworkCore;
using System.Net.Sockets;

var builder = Host.CreateApplicationBuilder(args);

// ── Database ──────────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddScoped<ICoinRepository, CoinRepository>();

// ── CoinGecko HTTP client ────────────────────────────────────────────────
// Custom SocketsHttpHandler is intentional: shorter pooled lifetime + NoDelay
// to mitigate stale connections / latency spikes on the CoinGecko side.
builder.Services.AddHttpClient<ICoinGeckoService, CoinGeckoService>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectTimeout = TimeSpan.FromSeconds(15),
        EnableMultipleHttp2Connections = false,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            await socket.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
    });

// ── Coin price + history workers ─────────────────────────────────────────
// Backfill runs once on startup to fill any gap in daily history for active coins.
builder.Services.AddSingleton<CoinHistoryBackfillService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CoinHistoryBackfillService>());

// Ongoing: reconciles top N, upserts coin rows, upserts today's history row each cycle.
builder.Services.AddHostedService<CoinPriceUpdateService>();

// ── Monero supply (via BTCPay Server's connected XMR daemon) ─────────────
// MoneroSupplyService reads BtcPay:BaseUrl + BtcPay:ApiKey from configuration
// internally; this just wires up the typed HttpClient and timeout.
// Only register the worker if BtcPay is configured — otherwise the service
// constructor would throw on first resolution.
var btcPay = builder.Configuration.GetSection("BtcPay");
if (!string.IsNullOrWhiteSpace(btcPay["BaseUrl"]) && !string.IsNullOrWhiteSpace(btcPay["ApiKey"]))
{
    builder.Services.AddHttpClient<IMoneroSupplyService, MoneroSupplyService>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(
            btcPay.GetValue<int?>("TimeoutSeconds") ?? 30);
    });

    builder.Services.AddHostedService<MoneroSupplyWorker>();
}
else
{
    Console.WriteLine("[startup] BtcPay:BaseUrl or BtcPay:ApiKey not configured — MoneroSupplyWorker is disabled.");
}

var host = builder.Build();
host.Run();