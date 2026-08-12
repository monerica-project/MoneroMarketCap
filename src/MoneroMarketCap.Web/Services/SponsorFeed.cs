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
public sealed class SponsorFeed : ISponsorFeed
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory httpFactory;
    private readonly string? url;
    private readonly TimeSpan ttl;
    private readonly SemaphoreSlim gate = new(1, 1);

    private string cache = string.Empty;
    private DateTime cachedAt = DateTime.MinValue;

    public SponsorFeed(IHttpClientFactory httpFactory, IConfiguration config)
    {
        this.httpFactory = httpFactory;
        this.url = config["Sponsors:SourceUrl"];
        this.ttl = TimeSpan.FromMinutes(config.GetValue<int>("Sponsors:CacheTtlMinutes", 60));
    }

    public async Task<string> GetRawJsonAsync(CancellationToken ct = default)
    {
        if (this.IsFresh())
        {
            return this.cache;
        }

        await this.gate.WaitAsync(ct);
        try
        {
            if (this.IsFresh())
            {
                return this.cache;
            }

            if (string.IsNullOrEmpty(this.url))
            {
                return this.cache;
            }

            var client = this.httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            this.cache = await client.GetStringAsync(this.url, ct);
            this.cachedAt = DateTime.UtcNow;
            return this.cache;
        }
        catch
        {
            return this.cache; // keep last-good on failure
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async Task<IReadOnlyList<SponsorItem>> GetSponsorsAsync(CancellationToken ct = default)
    {
        var json = await this.GetRawJsonAsync(ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<SponsorItem>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<SponsorItem>>(json, JsonOpts) ?? new List<SponsorItem>();
        }
        catch
        {
            return Array.Empty<SponsorItem>();
        }
    }

    private bool IsFresh() => !string.IsNullOrEmpty(this.cache) && DateTime.UtcNow - this.cachedAt < this.ttl;
}
