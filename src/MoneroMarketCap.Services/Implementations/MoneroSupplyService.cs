// Services/Implementations/MoneroSupplyService.cs
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Services.Interfaces;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MoneroMarketCap.Services.Implementations;

public class MoneroSupplyService : IMoneroSupplyService
{
    // Monero's emission schedule constants.
    // PreTailTotalXmr: Approximate total XMR emitted at the end of the main
    //   emission curve, when the tail emission started at block 2,641,623.
    // TailEmissionStartHeight: Block at which the fixed tail-emission kicked in.
    // TailRewardPerBlock: Fixed 0.6 XMR per block forever after tail start.
    private const decimal PreTailTotalXmr = 18_132_009m;
    private const ulong TailEmissionStartHeight = 2_641_623UL;
    private const decimal TailRewardPerBlock = 0.6m;

    private readonly HttpClient _http;
    private readonly ILogger<MoneroSupplyService> _logger;
    private readonly string _baseUrl;
    private readonly string _apiKey;

    public MoneroSupplyService(
        HttpClient http,
        IConfiguration config,
        ILogger<MoneroSupplyService> logger)
    {
        _http = http;
        _logger = logger;
        _baseUrl = config["BtcPay:BaseUrl"]?.TrimEnd('/')
            ?? throw new InvalidOperationException("BtcPay:BaseUrl not configured");
        _apiKey = config["BtcPay:ApiKey"]
            ?? throw new InvalidOperationException("BtcPay:ApiKey not configured");
    }

    public async Task<(ulong height, decimal supply)?> GetHeightAndSupplyAsync(
        CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Get,
                $"{_baseUrl}/api/v1/server/info");
            req.Headers.Authorization =
                new AuthenticationHeaderValue("token", _apiKey);

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "BTCPay /server/info returned {Status}", resp.StatusCode);
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("syncStatus", out var syncStatus))
                return null;

            foreach (var entry in syncStatus.EnumerateArray())
            {
                if (!entry.TryGetProperty("paymentMethodId", out var pmId))
                    continue;
                if (!string.Equals(pmId.GetString(), "XMR-CHAIN",
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!entry.TryGetProperty("summary", out var summary))
                    return null;

                var synced = summary.TryGetProperty("synced", out var s)
                    && s.GetBoolean();
                var daemonAvailable = summary.TryGetProperty("daemonAvailable", out var da)
                    && da.GetBoolean();

                if (!synced || !daemonAvailable)
                {
                    _logger.LogInformation(
                        "BTCPay XMR daemon not synced or unavailable; skipping.");
                    return null;
                }

                if (!summary.TryGetProperty("currentHeight", out var h)
                    || !h.TryGetUInt64(out var height))
                    return null;

                var supply = ComputeSupply(height);
                return (height, supply);
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch Monero supply from BTCPay.");
            return null;
        }
    }

    private static decimal ComputeSupply(ulong height)
    {
        if (height < TailEmissionStartHeight)
        {
            // We only ever run after tail started (Monero has been in tail
            // emission since mid-2022), but guard anyway. Returning the
            // pre-tail total is a conservative approximation — in practice
            // this branch should never execute in production.
            return PreTailTotalXmr;
        }

        var tailBlocks = (decimal)(height - TailEmissionStartHeight);
        return PreTailTotalXmr + tailBlocks * TailRewardPerBlock;
    }
}