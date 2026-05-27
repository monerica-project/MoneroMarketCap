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

// ── Fiat exchange rates (open.er-api.com) ────────────────────────────────
// Single source for USD-based FX. Refreshed by FiatRateUpdateWorker on the
// configured interval (default 15 min). Does not touch CoinGecko quota.
//
// Same SocketsHttpHandler trick as the CoinGecko client above: force IPv4
// (AddressFamily.InterNetwork) so we don't hang on this VPS's broken IPv6
// connectivity. Without this, the HttpClient picks IPv6, can't connect, and
// every refresh times out after 20s.
builder.Services.AddHttpClient<IFiatRateService, FiatRateService>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectTimeout = TimeSpan.FromSeconds(10),
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
builder.Services.AddHostedService<FiatRateUpdateWorker>();

// ── Historical FX rates (frankfurter.app, ECB reference rates) ───────────
// Daily snapshots stored in FiatRateHistory, used by the chart endpoint to
// render non-USD price history accurately (FX rates as they were on each day,
// not just today's rate applied across the board).
//
// Same IPv4-forcing handler as above for the VPS's IPv6 quirk.
// Free, no API key, no rate limit at this volume. Does NOT touch CoinGecko quota.
builder.Services.AddHttpClient<IFiatRateHistoryService, FiatRateHistoryService>()
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
builder.Services.AddHostedService<FiatRateHistoryWorker>();

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
