using System.ComponentModel;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace Ex02.DictionaryMcpServer;

/// <summary>Challenge: dictionary.read for the lookups, dictionary.write for AddWord.</summary>
public static class DictionaryScopes
{
    public const string Read = "dictionary.read";
    public const string Write = "dictionary.write";
    public static readonly string[] All = [Read, Write];

    public static bool Has(ClaimsPrincipal user, string scope) =>
        user.FindAll("scope").Concat(user.FindAll("scp"))
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(scope, StringComparer.Ordinal);
}

/// <summary>The same three tools, each behind its scope; bodies delegate to <see cref="DictionaryTools"/>.</summary>
[McpServerToolType]
public static class ProtectedDictionaryTools
{
    [McpServerTool(Name = "define_word"), Authorize(Policy = DictionaryScopes.Read)]
    [Description("Looks up the definition of a word in the dictionary.")]
    public static string DefineWord([Description("The word to define")] string word) =>
        DictionaryTools.DefineWord(word);

    [McpServerTool(Name = "get_synonyms"), Authorize(Policy = DictionaryScopes.Read)]
    [Description("Gets synonyms for a word from the dictionary.")]
    public static string GetSynonyms([Description("The word to find synonyms for")] string word) =>
        DictionaryTools.GetSynonyms(word);

    [McpServerTool(Name = "add_word"), Authorize(Policy = DictionaryScopes.Write)]
    [Description("Adds a new word with its definition and optional synonyms to the dictionary. Existing words are not overwritten.")]
    public static string AddWord(
        [Description("The word to add")] string word,
        [Description("Its definition")] string definition,
        [Description("Optional synonyms")] string[]? synonyms = null) =>
        DictionaryTools.AddWord(word, definition, synonyms);
}
