using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace MoneroMarketCap.Web.Services;

/// <summary>One block header as the node reports it.</summary>
public sealed record ExplorerBlockHeader(
    long Height, string Hash, string PrevHash, DateTime TimestampUtc, long SizeBytes, int TxCount,
    decimal RewardXmr, string Difficulty, long Nonce, int MajorVersion, int MinorVersion, string MinerTxHash, long Depth);

/// <summary>A block: its header plus the hashes of the transactions it contains.</summary>
public sealed record ExplorerBlock(ExplorerBlockHeader Header, IReadOnlyList<string> TxHashes);

/// <summary>One transaction. Amounts and addresses are hidden by Monero itself and are not shown.</summary>
public sealed record ExplorerTx(
    string Hash, bool InPool, long? BlockHeight, DateTime? TimestampUtc, long? Confirmations, int Version, long UnlockTime,
    bool IsCoinbase, decimal? FeeXmr, decimal? CoinbaseAmountXmr, int SizeBytes, int RctType, int RingSize,
    IReadOnlyList<string> KeyImages, IReadOnlyList<string> OutputKeys, string ExtraHex, bool DoubleSpendSeen);

/// <summary>Headline numbers for the explorer home page.</summary>
public sealed record ExplorerSummary(long Height, int PoolSize, string Difficulty, double HashrateMh, string TopHash);

/// <summary>
/// Read-only Monero block explorer data, taken from our own node's restricted RPC. Visitors never
/// talk to the node: the site asks it and caches the answers (a confirmed block or transaction never
/// changes, so those are kept for hours; the tip and the pool for seconds). A site-wide ceiling on
/// uncached node calls keeps a crawler or a script from using this site to flood the node.
/// </summary>
public sealed partial class MoneroExplorer
{
    public const int LatestBlocksCount = 25;
    public const int MaxPoolListed = 25;
    private const int NodeCallsPerMinute = 240;
    private const decimal AtomicPerXmr = 1_000_000_000_000m;

    private static readonly object BudgetSync = new();
    private static readonly Queue<DateTime> RecentCalls = new();

    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MoneroExplorer> _log;

    public MoneroExplorer(HttpClient http, IMemoryCache cache, ILogger<MoneroExplorer> log)
    {
        _http = http;
        _cache = cache;
        _log = log;
    }

    /// <summary>False when no node is configured: the explorer pages then say so instead of failing.</summary>
    public bool IsConfigured => _http.BaseAddress is not null;

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Hash64();

    public static bool IsHash(string? value) => value is not null && Hash64().IsMatch(value);

    public static bool IsHeight(string? value, out long height) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out height) && height >= 0 && height < 1_000_000_000;

    public Task<ExplorerSummary?> GetSummaryAsync(CancellationToken ct) =>
        CachedAsync("xp:summary", TimeSpan.FromSeconds(20), async () =>
        {
            using var doc = await GetAsync("/get_info", ct);
            if (doc is null)
            {
                return null;
            }

            var r = doc.RootElement;
            var difficulty = r.TryGetProperty("difficulty", out var d) && d.TryGetInt64(out var dv) ? dv : 0;
            return new ExplorerSummary(
                r.GetProperty("height").GetInt64(),
                r.TryGetProperty("tx_pool_size", out var p) ? p.GetInt32() : 0,
                difficulty.ToString("N0", CultureInfo.InvariantCulture),
                difficulty / 120d / 1_000_000d,
                r.TryGetProperty("top_block_hash", out var t) ? t.GetString() ?? string.Empty : string.Empty);
        });

    public async Task<IReadOnlyList<ExplorerBlockHeader>> GetLatestBlocksAsync(CancellationToken ct)
    {
        var summary = await GetSummaryAsync(ct);
        if (summary is null)
        {
            return [];
        }

        var top = summary.Height - 1;
        return await CachedAsync($"xp:latest:{top}", TimeSpan.FromMinutes(5), async () =>
        {
            using var doc = await RpcAsync("get_block_headers_range", new { start_height = Math.Max(0, top - LatestBlocksCount + 1), end_height = top }, ct);
            if (doc is null || !doc.RootElement.TryGetProperty("result", out var result) || !result.TryGetProperty("headers", out var headers))
            {
                return (IReadOnlyList<ExplorerBlockHeader>)[];
            }

            return (IReadOnlyList<ExplorerBlockHeader>)headers.EnumerateArray().Select(ParseHeader).OrderByDescending(h => h.Height).ToList();
        }) ?? [];
    }

    public Task<ExplorerBlock?> GetBlockAsync(string heightOrHash, CancellationToken ct)
    {
        object prm;
        string key;
        if (IsHash(heightOrHash))
        {
            key = heightOrHash.ToLowerInvariant();
            prm = new { hash = key };
        }
        else if (IsHeight(heightOrHash, out var height))
        {
            key = height.ToString(CultureInfo.InvariantCulture);
            prm = new { height };
        }
        else
        {
            return Task.FromResult<ExplorerBlock?>(null);
        }

        // Kept briefly: "depth" (confirmations) changes with every new block.
        return CachedAsync($"xp:block:{key}", TimeSpan.FromMinutes(2), async () =>
        {
            using var doc = await RpcAsync("get_block", prm, ct);
            if (doc is null || !doc.RootElement.TryGetProperty("result", out var result) || !result.TryGetProperty("block_header", out var header))
            {
                return null;
            }

            var hashes = result.TryGetProperty("tx_hashes", out var th) && th.ValueKind == JsonValueKind.Array
                ? th.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(IsHash).ToList()
                : [];
            return new ExplorerBlock(ParseHeader(header), hashes);
        });
    }

    public Task<ExplorerTx?> GetTransactionAsync(string hash, CancellationToken ct)
    {
        if (!IsHash(hash))
        {
            return Task.FromResult<ExplorerTx?>(null);
        }

        var key = hash.ToLowerInvariant();
        return CachedAsync($"xp:tx:{key}", TimeSpan.FromSeconds(45), async () =>
        {
            using var doc = await PostAsync("/get_transactions", new { txs_hashes = new[] { key }, decode_as_json = true }, ct);
            if (doc is null || !doc.RootElement.TryGetProperty("txs", out var txs) || txs.ValueKind != JsonValueKind.Array || txs.GetArrayLength() == 0)
            {
                return null;
            }

            return ParseTx(txs[0]);
        });
    }

    /// <summary>Hashes of transactions waiting for a block (newest node order), capped for display.</summary>
    public Task<IReadOnlyList<string>?> GetPoolHashesAsync(CancellationToken ct) =>
        CachedAsync("xp:pool", TimeSpan.FromSeconds(20), async () =>
        {
            using var doc = await GetAsync("/get_transaction_pool_hashes", ct);
            if (doc is null || !doc.RootElement.TryGetProperty("tx_hashes", out var hashes) || hashes.ValueKind != JsonValueKind.Array)
            {
                return (IReadOnlyList<string>)[];
            }

            return (IReadOnlyList<string>)hashes.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(IsHash).ToList();
        });

    private static ExplorerBlockHeader ParseHeader(JsonElement h) => new(
        h.GetProperty("height").GetInt64(),
        h.GetProperty("hash").GetString() ?? string.Empty,
        h.TryGetProperty("prev_hash", out var prev) ? prev.GetString() ?? string.Empty : string.Empty,
        DateTimeOffset.FromUnixTimeSeconds(h.GetProperty("timestamp").GetInt64()).UtcDateTime,
        h.TryGetProperty("block_size", out var size) ? size.GetInt64() : 0,
        h.TryGetProperty("num_txes", out var n) ? n.GetInt32() : 0,
        h.TryGetProperty("reward", out var reward) ? reward.GetDecimal() / AtomicPerXmr : 0m,
        h.TryGetProperty("difficulty", out var diff) && diff.TryGetInt64(out var dv) ? dv.ToString("N0", CultureInfo.InvariantCulture) : string.Empty,
        h.TryGetProperty("nonce", out var nonce) && nonce.TryGetInt64(out var nv) ? nv : 0,
        h.TryGetProperty("major_version", out var maj) ? maj.GetInt32() : 0,
        h.TryGetProperty("minor_version", out var min) ? min.GetInt32() : 0,
        h.TryGetProperty("miner_tx_hash", out var miner) ? miner.GetString() ?? string.Empty : string.Empty,
        h.TryGetProperty("depth", out var depth) && depth.TryGetInt64(out var dp) ? dp : 0);

    private static ExplorerTx? ParseTx(JsonElement e)
    {
        var hash = e.TryGetProperty("tx_hash", out var th) ? th.GetString() ?? string.Empty : string.Empty;
        if (!IsHash(hash) || !e.TryGetProperty("as_json", out var asJson) || string.IsNullOrWhiteSpace(asJson.GetString()))
        {
            return null;
        }

        using var body = JsonDocument.Parse(asJson.GetString()!);
        var j = body.RootElement;
        var inPool = e.TryGetProperty("in_pool", out var ip) && ip.GetBoolean();

        var keyImages = new List<string>();
        var ringSize = 0;
        var coinbase = false;
        if (j.TryGetProperty("vin", out var vin) && vin.ValueKind == JsonValueKind.Array)
        {
            foreach (var input in vin.EnumerateArray())
            {
                if (input.TryGetProperty("gen", out _))
                {
                    coinbase = true;
                }
                else if (input.TryGetProperty("key", out var key))
                {
                    keyImages.Add(key.TryGetProperty("k_image", out var ki) ? ki.GetString() ?? string.Empty : string.Empty);
                    if (key.TryGetProperty("key_offsets", out var offsets) && offsets.ValueKind == JsonValueKind.Array)
                    {
                        ringSize = Math.Max(ringSize, offsets.GetArrayLength());
                    }
                }
            }
        }

        var outputs = new List<string>();
        decimal coinbaseAtomic = 0m;
        if (j.TryGetProperty("vout", out var vout) && vout.ValueKind == JsonValueKind.Array)
        {
            foreach (var output in vout.EnumerateArray())
            {
                if (output.TryGetProperty("amount", out var amount) && amount.TryGetDecimal(out var a))
                {
                    coinbaseAtomic += a;
                }

                if (output.TryGetProperty("target", out var target))
                {
                    if (target.TryGetProperty("tagged_key", out var tagged) && tagged.TryGetProperty("key", out var tk))
                    {
                        outputs.Add(tk.GetString() ?? string.Empty);
                    }
                    else if (target.TryGetProperty("key", out var plain) && plain.ValueKind == JsonValueKind.String)
                    {
                        outputs.Add(plain.GetString() ?? string.Empty);
                    }
                }
            }
        }

        decimal? fee = null;
        var rctType = 0;
        if (j.TryGetProperty("rct_signatures", out var rct))
        {
            rctType = rct.TryGetProperty("type", out var type) ? type.GetInt32() : 0;
            if (rct.TryGetProperty("txnFee", out var txnFee) && txnFee.TryGetDecimal(out var f))
            {
                fee = f / AtomicPerXmr;
            }
        }

        var extra = new StringBuilder();
        if (j.TryGetProperty("extra", out var ex) && ex.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in ex.EnumerateArray().Take(512))
            {
                extra.Append(b.GetInt32().ToString("x2", CultureInfo.InvariantCulture));
            }
        }

        var hexLength = e.TryGetProperty("as_hex", out var ah) ? (ah.GetString() ?? string.Empty).Length : 0;
        if (hexLength == 0 && e.TryGetProperty("pruned_as_hex", out var ph))
        {
            hexLength = (ph.GetString() ?? string.Empty).Length + (e.TryGetProperty("prunable_as_hex", out var pr) ? (pr.GetString() ?? string.Empty).Length : 0);
        }

        return new ExplorerTx(
            hash,
            inPool,
            !inPool && e.TryGetProperty("block_height", out var bh) && bh.TryGetInt64(out var bhv) ? bhv : null,
            e.TryGetProperty("block_timestamp", out var bt) && bt.TryGetInt64(out var btv) && btv > 0 ? DateTimeOffset.FromUnixTimeSeconds(btv).UtcDateTime : null,
            e.TryGetProperty("confirmations", out var conf) && conf.TryGetInt64(out var cv) ? cv : null,
            j.TryGetProperty("version", out var ver) ? ver.GetInt32() : 0,
            j.TryGetProperty("unlock_time", out var ut) && ut.TryGetInt64(out var utv) ? utv : 0,
            coinbase,
            coinbase ? null : fee,
            coinbase ? coinbaseAtomic / AtomicPerXmr : null,
            hexLength / 2,
            rctType,
            ringSize,
            keyImages,
            outputs,
            extra.ToString(),
            e.TryGetProperty("double_spend_seen", out var ds) && ds.GetBoolean());
    }

    private async Task<T?> CachedAsync<T>(string key, TimeSpan ttl, Func<Task<T?>> load)
    {
        if (_cache.TryGetValue(key, out T? hit))
        {
            return hit;
        }

        var value = await load();
        if (value is not null)
        {
            _cache.Set(key, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl, Size = 1 });
        }

        return value;
    }

    private Task<JsonDocument?> RpcAsync(string method, object prm, CancellationToken ct) =>
        PostAsync("/json_rpc", new { jsonrpc = "2.0", id = "0", method, @params = prm }, ct);

    private Task<JsonDocument?> GetAsync(string path, CancellationToken ct) => SendAsync(() => _http.GetAsync(path, ct), ct);

    private Task<JsonDocument?> PostAsync(string path, object body, CancellationToken ct) =>
        SendAsync(() => _http.PostAsync(path, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), ct), ct);

    private async Task<JsonDocument?> SendAsync(Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        if (!IsConfigured || !TakeBudget())
        {
            return null;
        }

        try
        {
            using var response = await send();
            if (response.StatusCode != HttpStatusCode.OK)
            {
                _log.LogWarning("Monero node answered {Status}", (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _log.LogWarning("Monero node call failed: {Message}", ex.Message);
            return null;
        }
    }

    // At most NodeCallsPerMinute uncached calls a minute, whoever asks.
    private static bool TakeBudget()
    {
        var now = DateTime.UtcNow;
        lock (BudgetSync)
        {
            while (RecentCalls.Count > 0 && now - RecentCalls.Peek() > TimeSpan.FromMinutes(1))
            {
                RecentCalls.Dequeue();
            }

            if (RecentCalls.Count >= NodeCallsPerMinute)
            {
                return false;
            }

            RecentCalls.Enqueue(now);
            return true;
        }
    }
}
