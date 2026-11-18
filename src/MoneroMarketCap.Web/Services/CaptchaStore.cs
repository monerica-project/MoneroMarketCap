using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace MoneroMarketCap.Web.Services;

/// <summary>
/// Server-side store for the registration captcha. The answer never leaves the server:
/// the form only carries an unguessable random token. Each token is valid for a short
/// time and can be checked exactly ONCE (right or wrong), so a solved captcha can't be
/// replayed to mass-create accounts — unlike the old answer-in-a-TempData-cookie scheme,
/// where the same cookie + answer could be re-posted indefinitely.
/// </summary>
public interface ICaptchaStore
{
    /// <summary>Stores <paramref name="answer"/> and returns the token to embed in the form.</summary>
    string Create(int answer);

    /// <summary>
    /// True when the token exists, has not been used and <paramref name="answer"/> matches.
    /// The token is consumed by the call regardless of the outcome.
    /// </summary>
    bool TryConsume(string? token, int answer);
}

public sealed class CaptchaStore : ICaptchaStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Most tokens kept at once. Every visit to the sign-up page makes one, so a script loading
    /// that page in a loop could otherwise fill memory; when full, the oldest token is dropped
    /// (a person who left the form open that long just gets a new captcha).
    /// </summary>
    public const int DefaultMaxTokens = 10_000;

    private const string KeyPrefix = "captcha:";

    private readonly IMemoryCache cache;
    private readonly int maxTokens;
    private readonly object sync = new();

    // Keys in the order they were made. Used and expired ones stay here until pushed out,
    // which keeps this simple and still never holds more than maxTokens keys.
    private readonly Queue<string> order = new();

    public CaptchaStore(IMemoryCache cache)
        : this(cache, DefaultMaxTokens)
    {
    }

    public CaptchaStore(IMemoryCache cache, int maxTokens)
    {
        this.cache = cache;
        this.maxTokens = Math.Max(1, maxTokens);
    }

    public string Create(int answer)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var key = KeyPrefix + token;
        lock (this.sync)
        {
            while (this.order.Count >= this.maxTokens)
            {
                this.cache.Remove(this.order.Dequeue());
            }

            this.order.Enqueue(key);
            this.cache.Set(key, new Entry(answer), Lifetime);
        }

        return token;
    }

    public bool TryConsume(string? token, int answer)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 64)
        {
            return false;
        }

        var key = KeyPrefix + token;
        if (!this.cache.TryGetValue(key, out Entry? entry) || entry is null)
        {
            return false;
        }

        this.cache.Remove(key);

        // Atomic claim: if two requests race with the same token only one can win.
        if (Interlocked.Exchange(ref entry.Used, 1) != 0)
        {
            return false;
        }

        return entry.Answer == answer;
    }

    private sealed class Entry
    {
        public Entry(int answer) => this.Answer = answer;

        public int Answer { get; }

#pragma warning disable SA1401 // Interlocked needs a field
        public int Used;
#pragma warning restore SA1401
    }
}
