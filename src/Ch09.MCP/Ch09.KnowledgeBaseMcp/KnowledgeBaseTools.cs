using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Ch09.KnowledgeBaseMcp;

/// <summary>
/// MCP server tools for searching a knowledge base of Markdown documents.
/// This bridges to Chapter 10 (RAG) by showing simple keyword-based retrieval.
/// </summary>
[McpServerToolType]
public static class KnowledgeBaseTools
{
    private static readonly string DocsPath = Path.Combine(AppContext.BaseDirectory, "docs");

    /// <summary>
    /// Searches the knowledge base for documents matching a query.
    /// Performs simple keyword matching across all Markdown files.
    /// </summary>
    /// <param name="query">The search query to match against document content.</param>
    /// <returns>A formatted string with matching document excerpts.</returns>
    [McpServerTool(Name = "SearchKnowledgeBase")]
    [Description("Searches the Contoso company knowledge base for documents matching the query. Returns relevant excerpts from matching Markdown documents about company policies, procedures, and information.")]
    public static string SearchKnowledgeBase(
        [Description("The search query — keywords or phrases to find in the knowledge base (e.g., 'remote work policy', 'expense report', 'onboarding')")] string query)
    {
        if (!Directory.Exists(DocsPath))
        {
            return "Error: Knowledge base docs directory not found.";
        }

        string[] mdFiles = Directory.GetFiles(DocsPath, "*.md");

        if (mdFiles.Length == 0)
        {
            return "The knowledge base is empty — no documents found.";
        }

        string[] queryTerms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        List<(string FileName, string Excerpt)> matches = [];

        foreach (string filePath in mdFiles)
        {
            string content = File.ReadAllText(filePath);
            bool isMatch = queryTerms.Any(term =>
                content.Contains(term, StringComparison.OrdinalIgnoreCase));

            if (isMatch)
            {
                // Extract the first 500 characters as an excerpt
                string excerpt = content.Length > 500
                    ? string.Concat(content.AsSpan(0, 500), "...")
                    : content;

                matches.Add((Path.GetFileNameWithoutExtension(filePath), excerpt));
            }
        }

        if (matches.Count == 0)
        {
            return $"No documents found matching '{query}'. " +
                   $"Available topics: {string.Join(", ", mdFiles.Select(f => Path.GetFileNameWithoutExtension(f)))}";
        }

        return string.Join("\n\n---\n\n", matches.Select(m =>
            $"## {m.FileName}\n\n{m.Excerpt}"));
    }
}
