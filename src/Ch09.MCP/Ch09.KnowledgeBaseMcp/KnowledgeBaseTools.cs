using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Ch09.KnowledgeBaseMcp;

/// <summary>The Markdown files in docs/, read by name only from that folder.</summary>
public static class KnowledgeBaseStore
{
    public static string DocsPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "docs");

    public static IReadOnlyList<KbDocument> Load() =>
        Directory.Exists(DocsPath)
            ? [.. Directory.GetFiles(DocsPath, "*.md").Order(StringComparer.Ordinal)
                .Select(f => new KbDocument(Path.GetFileNameWithoutExtension(f), File.ReadAllText(f)))]
            : [];

    /// <summary>
    /// The document called <paramref name="name"/>, or null. The name is looked up
    /// among the files that exist; it is never joined into a path, so "../x" finds nothing.
    /// </summary>
    public static KbDocument? Find(string name) =>
        Load().FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The search tool: what a model calls. Keyword matching across all documents.
/// </summary>
[McpServerToolType]
public static class KnowledgeBaseTools
{
    [McpServerTool(Name = "SearchKnowledgeBase")]
    [Description("Searches the Contoso company knowledge base for documents matching the query. Returns relevant excerpts from matching Markdown documents about company policies, procedures, and information.")]
    public static string SearchKnowledgeBase(
        [Description("The search query: keywords or phrases to find in the knowledge base (e.g., 'remote work policy', 'expense report', 'onboarding')")] string query)
    {
        IReadOnlyList<KbDocument> documents = KnowledgeBaseStore.Load();
        if (documents.Count == 0)
            throw new McpException("The knowledge base is empty: no documents found.");

        IReadOnlyList<KbMatch> matches = KeywordMatcher.Search(documents, query);
        if (matches.Count == 0)
        {
            return $"No documents found matching '{query}'. " +
                   $"Available topics: {string.Join(", ", documents.Select(d => d.Name))}";
        }

        return string.Join("\n\n---\n\n", matches.Select(m =>
            $"## {m.Name} (terms matched: {m.TermsMatched})\n\n{m.Excerpt}"));
    }
}

/// <summary>
/// The same documents as MCP resources: what an application (not the model)
/// reads by URI, for example to index them. The RAG chapter starts from here.
/// </summary>
[McpServerResourceType]
public static class KnowledgeBaseResources
{
    [McpServerResource(UriTemplate = "kb://docs/{name}", Name = "KnowledgeBaseDocument", MimeType = "text/markdown")]
    [Description("One knowledge base document, by name (for example remote-work-policy).")]
    public static string GetDocument(string name) =>
        KnowledgeBaseStore.Find(name)?.Content
        ?? throw new McpException($"No knowledge base document named '{name}'.");
}
