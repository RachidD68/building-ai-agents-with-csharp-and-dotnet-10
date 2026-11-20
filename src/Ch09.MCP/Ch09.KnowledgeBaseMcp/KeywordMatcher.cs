namespace Ch09.KnowledgeBaseMcp;

/// <summary>One Markdown document of the knowledge base.</summary>
public sealed record KbDocument(string Name, string Content);

/// <summary>A document that matched, with how many distinct query terms it contains.</summary>
public sealed record KbMatch(string Name, int TermsMatched, string Excerpt);

/// <summary>
/// Keyword retrieval, on purpose: a document matches when it contains a query
/// term, and documents with more distinct terms rank first. It knows nothing
/// about meaning ("time off" never finds "PTO"); the RAG chapter replaces it
/// with embeddings behind the same tool.
/// </summary>
public static class KeywordMatcher
{
    public const int MinTermLength = 3;   // "a", "of", "to" would match every document
    public const int ExcerptLength = 500;

    public static IReadOnlyList<string> Terms(string query) =>
        [.. query.Split([' ', '\t', ',', '.', '?', '!', ';', ':', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= MinTermLength)
            .Select(t => t.ToLowerInvariant())
            .Distinct()];

    public static IReadOnlyList<KbMatch> Search(IEnumerable<KbDocument> documents, string query, int max = 3)
    {
        IReadOnlyList<string> terms = Terms(query);
        if (terms.Count == 0) return [];

        return [.. documents
            .Select(d => new KbMatch(
                d.Name,
                terms.Count(t => d.Content.Contains(t, StringComparison.OrdinalIgnoreCase)),
                d.Content.Length > ExcerptLength ? string.Concat(d.Content.AsSpan(0, ExcerptLength), "...") : d.Content))
            .Where(m => m.TermsMatched > 0)
            .OrderByDescending(m => m.TermsMatched)
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .Take(max)];
    }
}
