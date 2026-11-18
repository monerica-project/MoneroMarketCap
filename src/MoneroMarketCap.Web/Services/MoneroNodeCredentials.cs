using System.Net;

namespace MoneroMarketCap.Web.Services;

/// <summary>
/// Login for our Monero node. A bare NetworkCredential answers whatever scheme the server asks
/// for, Basic included, and Basic sends the password in plain base64. The node only speaks
/// Digest, so the password is offered for Digest alone and only to the configured node.
/// </summary>
public static class MoneroNodeCredentials
{
    public const string Scheme = "Digest";

    /// <summary>Null when no node URL is set: then there is nothing to log in to.</summary>
    public static ICredentials? DigestOnly(string? nodeUrl, string? username, string? password)
    {
        if (!Uri.TryCreate(nodeUrl, UriKind.Absolute, out var nodeUri))
        {
            return null;
        }

        var cache = new CredentialCache();
        cache.Add(
            new Uri(nodeUri.GetLeftPart(UriPartial.Authority)),
            Scheme,
            new NetworkCredential(username ?? string.Empty, password ?? string.Empty));
        return cache;
    }
}
