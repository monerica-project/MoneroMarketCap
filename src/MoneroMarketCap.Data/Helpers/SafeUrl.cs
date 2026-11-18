namespace MoneroMarketCap.Data.Helpers;

/// <summary>
/// Outbound links that come from another system (the SwapRaven catalog, exchange lists) are only
/// kept when they are absolute http or https addresses. Anything else, such as "javascript:" or
/// "data:", would run in our visitors' browsers when clicked, so it is dropped.
/// </summary>
public static class SafeUrl
{
    /// <summary>The trimmed link when it is an absolute http(s) URL, otherwise null.</summary>
    public static string? HttpOrNull(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        return trimmed;
    }

    /// <summary>Same as <see cref="HttpOrNull"/>, but an empty string instead of null.</summary>
    public static string HttpOrEmpty(string? value) => HttpOrNull(value) ?? string.Empty;
}
