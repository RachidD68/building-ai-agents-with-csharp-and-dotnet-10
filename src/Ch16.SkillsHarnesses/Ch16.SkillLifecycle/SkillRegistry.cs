using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;

namespace Ch16.SkillLifecycle;

/// <summary>What was reviewed: who owns the skill, which version, where it came from, and the hash of its content.</summary>
public sealed record SkillRecord(string Name, string Owner, string Version, string Origin, string Sha256,
    string? ApprovedBy = null, bool Withdrawn = false, string? WithdrawnReason = null);

/// <summary>A skill's hash plus one hash per file (relative path), taken from one read of the folder.</summary>
public sealed record SkillSnapshot(string Sha256, IReadOnlyDictionary<string, string> Files);

public sealed record ValidationResult(SkillRecord? Record, IReadOnlyList<string> Problems)
{
    public bool Ok => Problems.Count == 0;
}

/// <summary>Validates skills and keeps the approved records. The lock file is the reviewed state.</summary>
public sealed partial class SkillRegistry
{
    private readonly Dictionary<string, SkillRecord> _records = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public SkillRecord? Find(string name) { lock (_gate) return _records.GetValueOrDefault(name); }

    /// <summary>Owner and version in the frontmatter metadata, every referenced file present, and the content hash.</summary>
    public static async Task<ValidationResult> ValidateAsync(AgentSkill skill, CancellationToken ct = default)
    {
        List<string> problems = [];
        AgentSkillFrontmatter fm = skill.Frontmatter;
        string owner = fm.Metadata?.GetValueOrDefault("owner")?.ToString() ?? "";
        string version = fm.Metadata?.GetValueOrDefault("version")?.ToString() ?? "";
        if (owner.Length == 0) problems.Add("no metadata.owner");
        if (version.Length == 0) problems.Add("no metadata.version");

        // Origin: the folder as <skills root>/<skill>, so the lock file holds no machine path.
        string origin = skill is AgentFileSkill file
            ? $"{Path.GetFileName(Path.GetDirectoryName(file.Path))}/{Path.GetFileName(file.Path)}"
            : $"{skill.GetType().Name}:{fm.Name}";
        if (skill is AgentFileSkill f)
        {
            string body = await File.ReadAllTextAsync(Path.Combine(f.Path, "SKILL.md"), ct);
            string folder = Path.GetFullPath(f.Path) + Path.DirectorySeparatorChar;
            foreach (Match m in ReferencePattern().Matches(body))
            {
                string target = Path.GetFullPath(Path.Combine(f.Path, m.Value));
                if (!target.StartsWith(folder, StringComparison.Ordinal)) problems.Add($"outside the skill folder: {m.Value}");
                else if (!File.Exists(target)) problems.Add($"missing {m.Value}");
            }
        }
        string hash = await HashAsync(skill, ct);
        return new(problems.Count == 0 ? new SkillRecord(fm.Name, owner, version, origin, hash) : null, problems);
    }

    /// <summary>Approve a validated record. Re-approving replaces the old record (and clears a withdrawal).</summary>
    public void Approve(ValidationResult validation, string approvedBy)
    {
        if (!validation.Ok || validation.Record is null)
            throw new InvalidOperationException($"Cannot approve: {string.Join("; ", validation.Problems)}");
        lock (_gate) _records[validation.Record.Name] = validation.Record with { ApprovedBy = approvedBy };
    }

    /// <summary>Emergency withdrawal: takes effect on the next operation, in-flight runs included.</summary>
    public void Withdraw(string name, string reason)
    {
        lock (_gate)
        {
            if (_records.TryGetValue(name, out SkillRecord? r)) _records[name] = r with { Withdrawn = true, WithdrawnReason = reason };
        }
    }

    /// <summary>
    /// SHA-256 of the effective content. A file skill hashes every file under its folder
    /// (relative path + bytes); a code skill hashes the content the provider would serve.
    /// </summary>
    public static async Task<string> HashAsync(AgentSkill skill, CancellationToken ct = default) =>
        (await SnapshotAsync(skill, ct)).Sha256;

    /// <summary>The skill hash and, for a file skill, one hash per file, all from the same read.</summary>
    public static async Task<SkillSnapshot> SnapshotAsync(AgentSkill skill, CancellationToken ct = default)
    {
        using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Dictionary<string, string> files = new(StringComparer.Ordinal);
        if (skill is AgentFileSkill file)
        {
            foreach (string path in Directory.GetFiles(file.Path, "*", SearchOption.AllDirectories)
                         .Select(p => Path.GetRelativePath(file.Path, p).Replace('\\', '/')).Order(StringComparer.Ordinal))
            {
                byte[] content = Canonical(await File.ReadAllBytesAsync(Path.Combine(file.Path, path), ct));
                sha.AppendData(Encoding.UTF8.GetBytes($"{path}\0"));
                sha.AppendData(content);
                sha.AppendData([0]);
                files[path] = Convert.ToHexStringLower(SHA256.HashData(content));
            }
        }
        else
        {
            sha.AppendData(Encoding.UTF8.GetBytes(Normalize(await skill.GetContentAsync(ct))));
        }
        return new(Convert.ToHexStringLower(sha.GetHashAndReset()), files);
    }

    /// <summary>
    /// The bytes that are hashed: the raw bytes, except that a file that decodes as strict
    /// UTF-8 has CRLF turned into LF, so a Windows and a Linux checkout agree. A BOM, a
    /// binary file or any other byte change still changes the hash.
    /// </summary>
    public static byte[] Canonical(byte[] raw)
    {
        try { StrictUtf8.GetCharCount(raw); }
        catch (DecoderFallbackException) { return raw; }
        List<byte> lf = new(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == (byte)'\r' && i + 1 < raw.Length && raw[i + 1] == (byte)'\n') continue;
            lf.Add(raw[i]);
        }
        return [.. lf];
    }

    /// <summary>The hash of one file, as the snapshot records it.</summary>
    public static string FileHash(byte[] raw) => Convert.ToHexStringLower(SHA256.HashData(Canonical(raw)));

    /// <summary>A text resource as the model receives it: UTF-8, BOM dropped.</summary>
    public static string DecodeText(byte[] raw) => new UTF8Encoding(false).GetString(raw).TrimStart('\uFEFF');

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string Normalize(string text) => text.Replace("\r\n", "\n");

    public void Save(string path)
    {
        lock (_gate) File.WriteAllText(path, JsonSerializer.Serialize(_records.Values.OrderBy(r => r.Name), JsonOptions));
    }

    public static SkillRegistry Load(string path)
    {
        SkillRegistry registry = new();
        if (!File.Exists(path)) return registry;
        foreach (SkillRecord r in JsonSerializer.Deserialize<List<SkillRecord>>(File.ReadAllText(path)) ?? [])
            registry._records[r.Name] = r;
        return registry;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [GeneratedRegex(@"(?<![\w/])(references|scripts|assets)/[\w.\-/]+\w")]
    private static partial Regex ReferencePattern();
}
