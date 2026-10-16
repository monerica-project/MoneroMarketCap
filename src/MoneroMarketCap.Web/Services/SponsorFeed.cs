using System.Text.Json;

namespace MoneroMarketCap.Web.Services;

/// <summary>One sponsor as returned by the Monerica active-sponsor feed.</summary>
public sealed class SponsorItem
{
    public string Name { get; set; } = string.Empty;
    public string Link { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SponsorshipType { get; set; } = string.Empty;
    public DateTime? ExpirationDate { get; set; }
}

public interface ISponsorFeed
{
    /// <summary>The raw feed JSON (cached), served verbatim to the client.</summary>
    Task<string> GetRawJsonAsync(CancellationToken ct = default);

    /// <summary>Parsed sponsors (cached), for server-side rendering so no-JS visitors see them.</summary>
    Task<IReadOnlyList<SponsorItem>> GetSponsorsAsync(CancellationToken ct = default);
}

/// <summary>
/// Fetches the Monerica active-sponsor feed and caches it in memory. Single source
/// for both the /api/sponsors proxy and the server-rendered sponsor banner.
/// </summary>
/// <remarks>
/// Built so the feed can never stall page rendering:
///   * Stale-while-revalidate: once anything has been cached, callers get it immediately
///     and an expired entry is refreshed in the background (one refresh at a time).
///   * Cold start: with nothing cached yet a caller waits at most <see cref="MaxWait"/>
///     for the first fetch, then renders without sponsors (the fetch keeps running).
///   * Back-off: a failed fetch keeps the last good data and blocks retries for
///     <see cref="RetryAfterFailure"/>, instead of every request retrying (serially,
///     behind a lock, with a 10s timeout each) as it used to.
///   * Validation: only a 2xx JSON response whose body parses as a sponsor array is
///     cached. A 200 carrying an HTML error page is rejected, so /api/sponsors never
///     serves HTML labelled as JSON.
/// </remarks>
public sealed class SponsorFeed : ISponsorFeed
{
    /// <summary>Longest a request ever waits on the upstream feed.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(2);

    /// <summary>How long to wait before retrying after a failed fetch.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory httpFactory;
    private readonly string? url;
    private readonly TimeSpan ttl;
    private readonly TimeProvider clock;
    private readonly object sync = new();

    // Immutable snapshot of the last good feed, swapped atomically.
    private volatile Snapshot? current;
    private DateTimeOffset retryNotBefore = DateTimeOffset.MinValue;
    private Task? refreshing;

    public SponsorFeed(IHttpClientFactory httpFactory, IConfiguration config, TimeProvider? clock = null)
    {
        this.httpFactory = httpFactory;
        this.url = config["Sponsors:SourceUrl"];
        this.ttl = TimeSpan.FromMinutes(config.GetValue<int>("Sponsors:CacheTtlMinutes", 60));
        this.clock = clock ?? TimeProvider.System;
    }

    public async Task<string> GetRawJsonAsync(CancellationToken ct = default) =>
        (await this.GetSnapshotAsync(ct))?.Json ?? string.Empty;

    public async Task<IReadOnlyList<SponsorItem>> GetSponsorsAsync(CancellationToken ct = default) =>
        (await this.GetSnapshotAsync(ct))?.Items ?? Array.Empty<SponsorItem>();

    /// <summary>
    /// Validates a feed response body. Returns the parsed sponsors, or null when the body
    /// is not a JSON array of sponsor objects (e.g. an HTML error page).
    /// </summary>
    public static IReadOnlyList<SponsorItem>? TryParseFeed(string? contentType, string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        // Missing content-type is tolerated (some proxies drop it) — the body must still
        // parse. An explicit non-JSON type (text/html …) is rejected outright.
        if (!string.IsNullOrEmpty(contentType)
            && !contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            return JsonSerializer.Deserialize<List<SponsorItem>>(body, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<Snapshot?> GetSnapshotAsync(CancellationToken ct)
    {
        var snap = this.current;
        var now = this.clock.GetUtcNow();
        if (snap != null && now - snap.FetchedAt < this.ttl)
        {
            return snap;
        }

        var refresh = this.StartRefreshIfDue(now);

        if (snap != null || refresh == null)
        {
            // Serve what we have (possibly stale, possibly nothing) without waiting.
            return snap;
        }

        // Cold start: wait briefly for the first fetch, never longer than MaxWait.
        try
        {
            await refresh.WaitAsync(MaxWait, this.clock, ct);
        }
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException)
        {
        }

        return this.current;
    }

    /// <summary>Starts a background refresh unless one is running or we are backing off.</summary>
    private Task? StartRefreshIfDue(DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(this.url))
        {
            return null;
        }

        lock (this.sync)
        {
            if (this.refreshing != null)
            {
                return this.refreshing;
            }

            if (now < this.retryNotBefore)
            {
                return null;
            }

            // Not tied to any request's cancellation: the result is shared by everyone.
            this.refreshing = Task.Run(this.RefreshAsync);
            return this.refreshing;
        }
    }

    private async Task RefreshAsync()
    {
        var ok = false;
        try
        {
            using var cts = new CancellationTokenSource(FetchTimeout);
            var client = this.httpFactory.CreateClient();
            client.Timeout = FetchTimeout;

            using var response = await client.GetAsync(this.url, cts.Token);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cts.Token);
                var items = TryParseFeed(response.Content.Headers.ContentType?.MediaType, body);
                if (items != null)
                {
                    this.current = new Snapshot(body, items, this.clock.GetUtcNow());
                    ok = true;
                }
            }
        }
        catch
        {
            // Network error / timeout: handled below as a failure (keep last good).
        }
        finally
        {
            lock (this.sync)
            {
                if (!ok)
                {
                    this.retryNotBefore = this.clock.GetUtcNow() + RetryAfterFailure;
                }

                this.refreshing = null;
            }
        }
    }

    private sealed record Snapshot(string Json, IReadOnlyList<SponsorItem> Items, DateTimeOffset FetchedAt);
}
