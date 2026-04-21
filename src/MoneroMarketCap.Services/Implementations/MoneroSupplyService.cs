using Microsoft.Extensions.Logging;
using MoneroMarketCap.Services.Interfaces;
using System.Net.Http.Json;

public class MoneroSupplyService : IMoneroSupplyService
{
    private readonly HttpClient _http;
    private readonly ILogger<MoneroSupplyService> _logger;

    // Monero tail emission started at block 2,641,623 (approx May 2022).
    // Total emission up to that block was ~18,132,009 XMR.
    // After that, 0.6 XMR per block forever.
    private const ulong TailEmissionStartHeight = 2_641_623;
    private const decimal PreTailTotalXmr = 18_132_009.447893m;
    private const decimal TailRewardPerBlock = 0.6m;

    public MoneroSupplyService(HttpClient http, ILogger<MoneroSupplyService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<(ulong Height, decimal SupplyXmr)> GetHeightAndSupplyAsync(CancellationToken ct)
    {
        var payload = new { jsonrpc = "2.0", id = "0", method = "get_info" };
        using var resp = await _http.PostAsJsonAsync("json_rpc", payload, ct);
        resp.EnsureSuccessStatusCode();
        var env = await resp.Content.ReadFromJsonAsync<RpcEnvelope<GetInfoResult>>(cancellationToken: ct);
        var height = env!.Result!.Height;

        decimal supply;
        if (height <= TailEmissionStartHeight)
        {
            // Pre-tail: we'd need the exponential decay formula. In practice
            // you won't hit this branch — tail started in 2022.
            // Conservative approximation, or skip for dates before tail.
            throw new InvalidOperationException("Height precedes tail emission; use historical estimate.");
        }
        else
        {
            var tailBlocks = height - TailEmissionStartHeight;
            supply = PreTailTotalXmr + (tailBlocks * TailRewardPerBlock);
        }

        return (height, supply);
    }

    private sealed record RpcEnvelope<T>(T? Result);
    private sealed record GetInfoResult(ulong Height);
}