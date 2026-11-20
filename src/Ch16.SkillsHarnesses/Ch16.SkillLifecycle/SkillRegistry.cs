using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;

namespace Ch16.SkillLifecycle;

/// <summary>What was reviewed: who owns the skill, which version, where it came from, and the hash of its content.</summary>
public sealed record SkillRecord(string Name, string Owner, string Version, string Origin, string Sha256,
    string? ApprovedBy = null, bool Withdrawn = false, string? WithdrawnReason = null);

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
            foreach (Match m in ReferencePattern().Matches(body))
            {
                if (!File.Exists(Path.Combine(f.Path, m.Value))) problems.Add($"missing {m.Value}");
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
    /// (relative path + text, line endings normalized to LF so a Windows and a Linux
    /// checkout agree); a code skill hashes the content the provider would serve.
    /// </summary>
    public static async Task<string> HashAsync(AgentSkill skill, CancellationToken ct = default)
    {
        using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (skill is AgentFileSkill file)
        {
            foreach (string path in Directory.GetFiles(file.Path, "*", SearchOption.AllDirectories)
                         .Select(p => Path.GetRelativePath(file.Path, p).Replace('\\', '/')).Order(StringComparer.Ordinal))
            {
                string text = await File.ReadAllTextAsync(Path.Combine(file.Path, path), ct);
                sha.AppendData(Encoding.UTF8.GetBytes($"{path}\0{Normalize(text)}\0"));
            }
        }
        else
        {
            sha.AppendData(Encoding.UTF8.GetBytes(Normalize(await skill.GetContentAsync(ct))));
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

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
