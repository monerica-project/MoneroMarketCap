using System.Net;
using System.Text.RegularExpressions;

namespace MoneroMarketCap.Services.Display;

/// <summary>
/// Turns CoinGecko's raw description.en into safe, plain text for storage/display.
///
/// CoinGecko descriptions can contain HTML — mostly &lt;a href&gt; links back to
/// CoinGecko and external sites — plus \r\n line breaks and HTML entities. We do NOT
/// want outbound CoinGecko/affiliate links on MoneroMarketCap coin pages, and rendering
/// their raw HTML would be an XSS risk, so this strips all tags (keeping the anchor text),
/// decodes entities, and normalizes whitespace into blank-line-separated paragraphs.
/// The result is stored as plain text and rendered with paragraph breaks in the view.
/// </summary>
public static class CoinDescriptionSanitizer
{
    private static readonly Regex TagRe = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex MultiBlankRe = new(@"\n{3,}", RegexOptions.Compiled);
    private static readonly Regex TrailingSpacesRe = new(@"[ \t]+\n", RegexOptions.Compiled);

    /// <summary>
    /// Returns clean plain text, or empty string if the input has no real content.
    /// Never returns null so callers can treat "" as "fetched, but no description".
    /// </summary>
    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var text = raw;

        // Normalize the escaped and literal newlines CoinGecko mixes in.
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");

        // Common block tags become newlines before we strip everything else, so
        // paragraph structure survives (opening AND closing forms).
        text = Regex.Replace(text, @"<\s*br\s*/?\s*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*/?\s*(p|div|li|ul|ol|h[1-6])\b[^>]*>", "\n\n", RegexOptions.IgnoreCase);

        // Drop every remaining tag (keeps the visible text of <a>…</a>).
        text = TagRe.Replace(text, string.Empty);

        // Decode entities (&amp; &#39; etc.).
        text = WebUtility.HtmlDecode(text);

        // Whitespace tidy-up.
        text = TrailingSpacesRe.Replace(text, "\n");
        text = MultiBlankRe.Replace(text, "\n\n");
        text = text.Trim();

        return text;
    }

    /// <summary>Splits cleaned text into paragraphs for rendering as separate blocks.</summary>
    public static IReadOnlyList<string> ToParagraphs(string? cleaned)
    {
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return System.Array.Empty<string>();
        }

        return cleaned
            .Split('\n', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)
            .ToList();
    }
}
