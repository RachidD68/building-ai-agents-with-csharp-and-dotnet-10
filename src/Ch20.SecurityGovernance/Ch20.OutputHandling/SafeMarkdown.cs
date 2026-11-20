using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Ch20.OutputHandling;

/// <summary>
/// Keeps a Markdown link or image only when its URL is absolute https and its
/// host is on the allow-list (exact match or a subdomain). The answer is parsed
/// the way a renderer parses it (Markdig: CommonMark plus bare-URL autolinks),
/// so every form a renderer would follow is checked: inline links and images
/// with or without a title, reference-style links and their definitions, and
/// autolinks. Raw HTML is removed whole, since it can carry its own src.
/// Images to other hosts are dropped: a browser fetches them on render, so the
/// URL itself is the leak. What replaces a cut is escaped, so it renders as text
/// and cannot pair with the source around it into a new link or image.
/// </summary>
public sealed class SafeMarkdown(IEnumerable<string> allowedHosts)
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UsePreciseSourceLocation().UseAutoLinks().Build();

    private readonly HashSet<string> _allowed = new(allowedHosts, StringComparer.OrdinalIgnoreCase);

    public string Render(string markdown)
    {
        MarkdownDocument document = Markdown.Parse(markdown, Pipeline);
        List<(SourceSpan Span, string Text)> cuts = [];
        foreach (MarkdownObject node in document.Descendants())
        {
            switch (node)
            {
                case LinkInline link when Refusal(link.Url) is { } refusal:
                    string label = string.Concat(link.Descendants<LiteralInline>().Select(l => l.Content.ToString()));
                    cuts.Add((link.Span, link.IsImage ? Marker("image removed", refusal)
                        : link.IsAutoLink ? Marker("link removed", refusal)
                        : $"{Escape(label)} {Marker("link removed", refusal)}"));
                    break;
                case AutolinkInline auto when Refusal(auto.Url) is { } refusal:
                    cuts.Add((auto.Span, Marker("link removed", refusal)));
                    break;
                case LinkReferenceDefinition definition when Refusal(definition.Url) is { } refusal:
                    cuts.Add((definition.Span, Marker("link definition removed", refusal)));
                    break;
                case HtmlInline or HtmlBlock:
                    cuts.Add((node.Span, Marker("html removed", null)));
                    break;
            }
        }

        // Rewrite the source from the end; a cut inside one already made is skipped.
        string result = markdown;
        int floor = int.MaxValue;
        foreach ((SourceSpan span, string text) in cuts.OrderByDescending(c => c.Span.End).ThenBy(c => c.Span.Start))
        {
            if (span.End >= floor) continue;
            result = string.Concat(result.AsSpan(0, span.Start), text, result.AsSpan(span.End + 1));
            floor = span.Start;
        }
        return result;
    }

    // "\[image removed: host\]" renders as [image removed: host]. The escaped
    // brackets cannot open a link, so a '!' before the cut or a "(url)" after it
    // stays text, and a second Render finds nothing left to cut.
    private static string Marker(string what, string? why) =>
        why is null ? $@"\[{what}\]" : $@"\[{what}: {Escape(why)}\]";

    // Backslash-escape what CommonMark would read as markup in a label or a host.
    private static string Escape(string text) =>
        string.Concat(text.Select(c => @"\`*_[]()<>!&~#|".Contains(c) ? $@"\{c}" : c.ToString()));

    /// <summary>The HTML a CommonMark renderer makes of <paramref name="markdown"/>, for tests.</summary>
    public static string ToHtml(string markdown) => Markdown.ToHtml(markdown, Pipeline);

    private string? Refusal(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            return "not https";
        string host = uri.IdnHost;
        bool allowed = _allowed.Any(a => host.Equals(a, StringComparison.OrdinalIgnoreCase) ||
                                         host.EndsWith("." + a, StringComparison.OrdinalIgnoreCase));
        return allowed ? null : host;
    }

    /// <summary>A substring check of the kind that ships by accident, kept only to reproduce its defect.</summary>
    public static bool NaiveSubstringCheck(string url, string allowedHost) =>
        url.Contains(allowedHost, StringComparison.OrdinalIgnoreCase);
}
