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
/// Headline calls (summary, latest blocks, pool) and visitor lookups (a block or transaction by
/// height or hash) have separate ceilings, so someone typing endless made-up hashes can only use
/// up the lookup ceiling and the explorer home page keeps working for everyone else.
/// </summary>
public sealed partial class MoneroExplorer
{
    public const int LatestBlocksCount = 25;
    public const int MaxPoolListed = 25;
    // The old single ceiling was 240 a minute for everything; it is now split in two.
    public const int HeadlineCallsPerMinute = 60;
    public const int LookupCallsPerMinute = 180;
    private const decimal AtomicPerXmr = 1_000_000_000_000m;

    // Lookups the node answered with "no such block/transaction" are remembered for a minute,
    // so asking for the same made-up hash again costs no node call.
    public static readonly TimeSpan NotFoundTtl = TimeSpan.FromSeconds(60);
    public const int MaxRememberedNotFound = 5000;

    private static readonly ExplorerCallBudget HeadlineBudget = new(HeadlineCallsPerMinute);
    private static readonly ExplorerCallBudget LookupBudget = new(LookupCallsPerMinute);
    private static readonly ExplorerNotFoundCache NotFound = new(MaxRememberedNotFound, NotFoundTtl);

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
            using var doc = await GetAsync("/get_info", HeadlineBudget, ct);
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
            using var doc = await RpcAsync("get_block_headers_range", new { start_height = Math.Max(0, top - LatestBlocksCount + 1), end_height = top }, HeadlineBudget, ct);
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

        var cacheKey = $"xp:block:{key}";
        if (NotFound.Contains(cacheKey, DateTime.UtcNow))
        {
            return Task.FromResult<ExplorerBlock?>(null);
        }

        // Kept briefly: "depth" (confirmations) changes with every new block.
        return CachedAsync(cacheKey, TimeSpan.FromMinutes(2), async () =>
        {
            using var doc = await RpcAsync("get_block", prm, LookupBudget, ct);
            if (doc is null)
            {
                // Node unreachable or the ceiling was reached: not a real "not found", so not remembered.
                return null;
            }

            if (!doc.RootElement.TryGetProperty("result", out var result) || !result.TryGetProperty("block_header", out var header))
            {
                NotFound.Add(cacheKey, DateTime.UtcNow);
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
        var cacheKey = $"xp:tx:{key}";
        if (NotFound.Contains(cacheKey, DateTime.UtcNow))
        {
            return Task.FromResult<ExplorerTx?>(null);
        }

        return CachedAsync(cacheKey, TimeSpan.FromSeconds(45), async () =>
        {
            using var doc = await PostAsync("/get_transactions", new { txs_hashes = new[] { key }, decode_as_json = true }, LookupBudget, ct);
            if (doc is null)
            {
                // Node unreachable or the ceiling was reached: not a real "not found", so not remembered.
                return null;
            }

            if (!doc.RootElement.TryGetProperty("txs", out var txs) || txs.ValueKind != JsonValueKind.Array || txs.GetArrayLength() == 0)
            {
                NotFound.Add(cacheKey, DateTime.UtcNow);
                return null;
            }

            return ParseTx(txs[0]);
        });
    }

    /// <summary>Hashes of transactions waiting for a block (newest node order), capped for display.</summary>
    public Task<IReadOnlyList<string>?> GetPoolHashesAsync(CancellationToken ct) =>
        CachedAsync("xp:pool", TimeSpan.FromSeconds(20), async () =>
        {
            using var doc = await GetAsync("/get_transaction_pool_hashes", HeadlineBudget, ct);
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

    private Task<JsonDocument?> RpcAsync(string method, object prm, ExplorerCallBudget budget, CancellationToken ct) =>
        PostAsync("/json_rpc", new { jsonrpc = "2.0", id = "0", method, @params = prm }, budget, ct);

    private Task<JsonDocument?> GetAsync(string path, ExplorerCallBudget budget, CancellationToken ct) =>
        SendAsync(() => _http.GetAsync(path, ct), budget, ct);

    private Task<JsonDocument?> PostAsync(string path, object body, ExplorerCallBudget budget, CancellationToken ct) =>
        SendAsync(() => _http.PostAsync(path, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), ct), budget, ct);

    private async Task<JsonDocument?> SendAsync(Func<Task<HttpResponseMessage>> send, ExplorerCallBudget budget, CancellationToken ct)
    {
        if (!IsConfigured || !budget.TryTake(DateTime.UtcNow))
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
}

/// <summary>At most a set number of uncached node calls a minute, whoever asks.</summary>
public sealed class ExplorerCallBudget
{
    private readonly object _sync = new();
    private readonly Queue<DateTime> _recent = new();
    private readonly int _perMinute;

    public ExplorerCallBudget(int perMinute) => _perMinute = perMinute;

    public bool TryTake(DateTime now)
    {
        lock (_sync)
        {
            while (_recent.Count > 0 && now - _recent.Peek() > TimeSpan.FromMinutes(1))
            {
                _recent.Dequeue();
            }

            if (_recent.Count >= _perMinute)
            {
                return false;
            }

            _recent.Enqueue(now);
            return true;
        }
    }
}

/// <summary>
/// Keys the node recently answered "not found" for. Bounded on purpose: made-up hashes are
/// endless, so when it is full the oldest entry is dropped instead of growing without limit.
/// </summary>
public sealed class ExplorerNotFoundCache
{
    private readonly object _sync = new();
    private readonly Dictionary<string, DateTime> _until = new(StringComparer.Ordinal);
    private readonly Queue<(string Key, DateTime Until)> _order = new();
    private readonly int _capacity;
    private readonly TimeSpan _ttl;

    public ExplorerNotFoundCache(int capacity, TimeSpan ttl)
    {
        _capacity = Math.Max(1, capacity);
        _ttl = ttl;
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _until.Count;
            }
        }
    }

    public bool Contains(string key, DateTime now)
    {
        lock (_sync)
        {
            if (!_until.TryGetValue(key, out var until))
            {
                return false;
            }

            if (until > now)
            {
                return true;
            }

            _until.Remove(key);
            return false;
        }
    }

    public void Add(string key, DateTime now)
    {
        lock (_sync)
        {
            if (_until.TryGetValue(key, out var existing) && existing > now)
            {
                return;
            }

            // Entries go in with the same lifetime, so the oldest are always at the front:
            // first drop the ones that have run out, then the oldest live ones if still full.
            while (_order.Count > 0 && _order.Peek().Until <= now)
            {
                Drop(_order.Dequeue());
            }

            while (_until.Count >= _capacity && _order.Count > 0)
            {
                Drop(_order.Dequeue());
            }

            var until = now + _ttl;
            _until[key] = until;
            _order.Enqueue((key, until));
        }
    }

    // Removes the entry only if it is still the one this queue item was made for.
    private void Drop((string Key, DateTime Until) item)
    {
        if (_until.TryGetValue(item.Key, out var until) && until == item.Until)
        {
            _until.Remove(item.Key);
        }
    }
}
