using System.Text.RegularExpressions;

namespace Ch20.OutputHandling;

/// <summary>
/// Keeps a Markdown link or image only when its URL is absolute https and its
/// host is on the allow-list (exact match or a subdomain). Everything else is
/// replaced by its label and the reason. Images to other hosts are dropped
/// whole: a browser fetches them on render, so the URL itself is the leak.
/// </summary>
public sealed partial class SafeMarkdown(IEnumerable<string> allowedHosts)
{
    private readonly HashSet<string> _allowed = new(allowedHosts, StringComparer.OrdinalIgnoreCase);

    public string Render(string markdown) => LinkOrImage().Replace(markdown, m =>
    {
        bool image = m.Groups["bang"].Success;
        string label = m.Groups["label"].Value, url = m.Groups["url"].Value;
        string? refusal = Refusal(url);
        if (refusal is null) return m.Value;
        return image ? $"[image removed: {refusal}]" : $"{label} [link removed: {refusal}]";
    });

    private string? Refusal(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            return "not https";
        string host = uri.IdnHost;
        bool allowed = _allowed.Any(a => host.Equals(a, StringComparison.OrdinalIgnoreCase) ||
                                         host.EndsWith("." + a, StringComparison.OrdinalIgnoreCase));
        return allowed ? null : host;
    }

    /// <summary>A substring check of the kind that ships by accident, kept only to reproduce its defect.</summary>
    public static bool NaiveSubstringCheck(string url, string allowedHost) =>
        url.Contains(allowedHost, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"(?<bang>!)?\[(?<label>[^\]]*)\]\((?<url>[^()\s]*(?:\([^()\s]*\)[^()\s]*)*)\)")]
    private static partial Regex LinkOrImage();
}
