using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Ex02.DictionaryMcpServer;

/// <summary>
/// MCP tools for dictionary lookups: DefineWord and GetSynonyms, backed by a
/// small in-memory dictionary. Two static methods, one dictionary — the same
/// shape as ProductLookup and InventoryCheck in Ch09.McpServer.
/// </summary>
[McpServerToolType]
public static class DictionaryTools
{
    // A small in-memory dictionary — ten to twenty entries is plenty.
    private static readonly Dictionary<string, DictionaryEntry> Dictionary =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["reticent"] = new("Reluctant to speak or reveal one's thoughts; reserved.",
                ["reserved", "taciturn", "tight-lipped", "uncommunicative"]),
            ["ephemeral"] = new("Lasting for a very short time.",
                ["fleeting", "transient", "short-lived", "momentary"]),
            ["ubiquitous"] = new("Present, appearing, or found everywhere.",
                ["omnipresent", "pervasive", "universal"]),
            ["pragmatic"] = new("Dealing with things sensibly and realistically rather than theoretically.",
                ["practical", "realistic", "sensible", "down-to-earth"]),
            ["verbose"] = new("Using or expressed in more words than are needed.",
                ["wordy", "long-winded", "garrulous", "rambling"]),
            ["meticulous"] = new("Showing great attention to detail; very careful and precise.",
                ["careful", "precise", "thorough", "painstaking"]),
            ["resilient"] = new("Able to withstand or recover quickly from difficult conditions.",
                ["tough", "adaptable", "hardy", "buoyant"]),
            ["candid"] = new("Truthful and straightforward; frank.",
                ["frank", "honest", "direct", "forthright"]),
            ["diligent"] = new("Having or showing care and conscientiousness in one's work or duties.",
                ["industrious", "assiduous", "hardworking", "conscientious"]),
            ["ambiguous"] = new("Open to more than one interpretation; not having one obvious meaning.",
                ["unclear", "equivocal", "vague", "cryptic"]),
            ["concise"] = new("Giving a lot of information clearly and in a few words.",
                ["succinct", "terse", "compact", "pithy"]),
            ["obsolete"] = new("No longer produced or used; out of date.",
                ["outdated", "antiquated", "deprecated", "archaic"]),
            ["robust"] = new("Strong and healthy; able to withstand adverse conditions.",
                ["sturdy", "durable", "hardy", "vigorous"]),
            ["novel"] = new("New or unusual in an interesting way.",
                ["new", "original", "innovative", "fresh"]),
        };

    /// <summary>
    /// Looks up the definition of a word.
    /// </summary>
    /// <param name="word">The word to look up.</param>
    /// <returns>The definition or a not-found message.</returns>
    [McpServerTool]
    [Description("Looks up the definition of a word in the dictionary.")]
    public static string DefineWord(
        [Description("The word to define")] string word)
    {
        if (Dictionary.TryGetValue(word.Trim(), out DictionaryEntry? entry))
        {
            return $"{word}: {entry.Definition}";
        }

        string available = string.Join(", ", Dictionary.Keys.Order());
        return $"'{word}' not found in dictionary. Available words: {available}";
    }

    /// <summary>
    /// Gets synonyms for a word.
    /// </summary>
    /// <param name="word">The word to find synonyms for.</param>
    /// <returns>A list of synonyms or a not-found message.</returns>
    [McpServerTool]
    [Description("Gets synonyms for a word from the dictionary.")]
    public static string GetSynonyms(
        [Description("The word to find synonyms for")] string word)
    {
        if (Dictionary.TryGetValue(word.Trim(), out DictionaryEntry? entry))
        {
            return entry.Synonyms.Length > 0
                ? $"Synonyms for '{word}': {string.Join(", ", entry.Synonyms)}"
                : $"No synonyms found for '{word}'.";
        }

        string available = string.Join(", ", Dictionary.Keys.Order());
        return $"'{word}' not found in dictionary. Available words: {available}";
    }
}

/// <summary>
/// A dictionary entry with a definition and its synonyms.
/// </summary>
/// <param name="Definition">The definition of the word.</param>
/// <param name="Synonyms">Synonyms for the word.</param>
sealed record DictionaryEntry(string Definition, string[] Synonyms);
