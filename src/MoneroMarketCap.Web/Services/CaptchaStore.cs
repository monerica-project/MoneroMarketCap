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

    private const string KeyPrefix = "captcha:";

    private readonly IMemoryCache cache;

    public CaptchaStore(IMemoryCache cache) => this.cache = cache;

    public string Create(int answer)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        this.cache.Set(KeyPrefix + token, new Entry(answer), Lifetime);
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
