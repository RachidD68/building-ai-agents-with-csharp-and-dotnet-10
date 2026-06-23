using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Ex02.DictionaryMcpServer;

/// <summary>
/// MCP tools for dictionary lookups: definitions and synonyms.
/// </summary>
[McpServerToolType]
public static class DictionaryTools
{
    private static readonly Lazy<Dictionary<string, DictionaryEntry>> Dictionary = new(LoadDictionary);

    /// <summary>
    /// Looks up the definition of a word.
    /// </summary>
    /// <param name="word">The word to look up.</param>
    /// <returns>The definition or a not-found message.</returns>
    [McpServerTool]
    [Description("Looks up the definition of a technical term in the software engineering dictionary.")]
    public static string DefineWord(
        [Description("The word or term to look up")] string word)
    {
        string normalized = word.Trim().ToLowerInvariant();

        if (Dictionary.Value.TryGetValue(normalized, out DictionaryEntry? entry))
        {
            return $"{word}: {entry.Definition}";
        }

        string available = string.Join(", ", Dictionary.Value.Keys.Order());
        return $"'{word}' not found in dictionary. Available terms: {available}";
    }

    /// <summary>
    /// Finds synonyms for a word.
    /// </summary>
    /// <param name="word">The word to find synonyms for.</param>
    /// <returns>A list of synonyms or a not-found message.</returns>
    [McpServerTool]
    [Description("Finds synonyms for a technical term in the software engineering dictionary.")]
    public static string FindSynonyms(
        [Description("The word or term to find synonyms for")] string word)
    {
        string normalized = word.Trim().ToLowerInvariant();

        if (Dictionary.Value.TryGetValue(normalized, out DictionaryEntry? entry))
        {
            if (entry.Synonyms is { Count: > 0 })
            {
                return $"Synonyms for '{word}': {string.Join(", ", entry.Synonyms)}";
            }
            return $"No synonyms found for '{word}'.";
        }

        string available = string.Join(", ", Dictionary.Value.Keys.Order());
        return $"'{word}' not found in dictionary. Available terms: {available}";
    }

    private static Dictionary<string, DictionaryEntry> LoadDictionary()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "dictionary.json");

        if (!File.Exists(path))
        {
            return new Dictionary<string, DictionaryEntry>();
        }

        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, DictionaryEntry>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new Dictionary<string, DictionaryEntry>();
    }
}

/// <summary>
/// A dictionary entry with definition and synonyms.
/// </summary>
sealed class DictionaryEntry
{
    /// <summary>The definition of the term.</summary>
    public string Definition { get; set; } = string.Empty;

    /// <summary>Synonyms for the term.</summary>
    public List<string>? Synonyms { get; set; }
}
