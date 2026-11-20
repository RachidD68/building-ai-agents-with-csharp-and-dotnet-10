using System.Globalization;

namespace Ch11.AuthorizedSearch;

/// <summary>The lowest level that may read a document. Staff &lt; Manager &lt; Restricted.</summary>
public enum AccessLevel { Staff = 0, Manager = 1, Restricted = 2 }

/// <summary>The state the source system reports for one document version.</summary>
public enum SourceState { Active, Superseded, Draft, Revoked }

/// <summary>
/// Who is asking, as the host authenticated them. It is built by the host for each
/// request and passed in; nothing reads it from a shared field.
/// </summary>
public sealed record CallerContext(string Tenant, string Subject, AccessLevel Level)
{
    public bool IsComplete => Tenant.Length > 0 && Subject.Length > 0;
}

/// <summary>One version of one policy document, with its access metadata.</summary>
public sealed record PolicyDocument(
    string DocId, int Version, string Tenant, AccessLevel Access, SourceState State,
    DateOnly EffectiveFrom, string Title, string Body, string SourcePath)
{
    /// <summary>Paragraphs are the chunks: small, stable, and each one has an index that never moves.</summary>
    public IReadOnlyList<string> Paragraphs() =>
        Body.Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The citation id of one paragraph: document, version and chunk, never a list position.</summary>
    public string EvidenceId(int chunk) => $"{DocId}@v{Version}#{chunk}";
}

/// <summary>Reads the synthetic corpus: Markdown files with a small front-matter block.</summary>
public static class Corpus
{
    public static string DefaultFolder => Path.Combine(AppContext.BaseDirectory, "corpus");

    public static IReadOnlyList<PolicyDocument> Load(string? folder = null)
    {
        folder ??= DefaultFolder;
        return [.. Directory.GetFiles(folder, "*.v*.md").Order(StringComparer.Ordinal).Select(Parse)];
    }

    public static string Version(string? folder = null) =>
        File.ReadAllText(Path.Combine(folder ?? DefaultFolder, "VERSION")).Trim();

    public static PolicyDocument Parse(string path)
    {
        string text = File.ReadAllText(path).Replace("\r\n", "\n");
        string[] parts = text.Split("---\n", 3);
        if (parts.Length != 3 || parts[0].Length != 0)
            throw new FormatException($"{Path.GetFileName(path)}: no front matter");
        Dictionary<string, string> meta = parts[1]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(':', 2))
            .ToDictionary(kv => kv[0].Trim(), kv => kv[1].Trim());
        return new PolicyDocument(
            meta["doc"],
            int.Parse(meta["version"], CultureInfo.InvariantCulture),
            meta["tenant"],
            Enum.Parse<AccessLevel>(meta["access"], ignoreCase: true),
            Enum.Parse<SourceState>(meta["state"], ignoreCase: true),
            DateOnly.ParseExact(meta["effective"], "yyyy-MM-dd", CultureInfo.InvariantCulture),
            meta["title"],
            parts[2].Trim(),
            Path.GetFileName(path));
    }
}
