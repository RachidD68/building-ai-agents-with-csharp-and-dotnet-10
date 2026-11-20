using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Ch16.SkillLifecycle;

namespace Ch23.QualificationRunner;

public sealed record PackagePin(string Id, string Version, bool Prerelease);
public sealed record LockFileHash(string Project, string Sha256);
public sealed record PromptHash(string Id, string Sha256, int Chars);
public sealed record SkillHash(string Name, string Version, string Sha256, string? Locked, bool Match);
public sealed record DataHash(string Id, string Path, int Files, string Sha256);

/// <summary>
/// The candidate's identity, computed from the repo, never typed: SDK request,
/// package pins, one hash per lock file, the compiled prompts, the skill content
/// hashes against their lock, and the data versions. Problems refuse the campaign.
/// </summary>
public sealed record CandidateIdentity
{
    public required string Candidate { get; init; }
    public required string ManifestSha256 { get; init; }
    public required string GlobalJsonSdk { get; init; }
    public required List<PackagePin> Packages { get; init; }
    public required List<LockFileHash> LockFiles { get; init; }
    public required List<PromptHash> Prompts { get; init; }
    public required List<SkillHash> Skills { get; init; }
    public required List<DataHash> Data { get; init; }
    public required ModelProfile Profile { get; init; }
    public required List<string> Problems { get; init; }

    /// <summary>One hash over everything above except the problems: equal hashes, same candidate.</summary>
    public string Sha256 => Hex(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
        new { Candidate, ManifestSha256, GlobalJsonSdk, Packages, LockFiles, Prompts, Skills, Data, Profile })));

    public static string Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static async Task<CandidateIdentity> ComputeAsync(string repo, string manifestPath, CandidateManifest m, bool promptsBuilt)
    {
        List<string> problems = [];
        string sdk = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, "global.json")))
            .RootElement.GetProperty("sdk").GetProperty("version").GetString() ?? "";

        // Versions come from Directory.Packages.props only; a lock file that resolved another one is drift.
        Dictionary<string, string> props = XDocument.Load(Path.Combine(repo, "Directory.Packages.props"))
            .Descendants("PackageVersion")
            .ToDictionary(e => (string)e.Attribute("Include")!, e => (string)e.Attribute("Version")!, StringComparer.OrdinalIgnoreCase);
        SortedDictionary<string, string> direct = new(StringComparer.OrdinalIgnoreCase);
        List<LockFileHash> locks = [];
        foreach (string project in Closure(repo, m.Identity.Projects))
        {
            string lockPath = Path.Combine(Path.GetDirectoryName(Path.Combine(repo, project))!, "packages.lock.json");
            if (!File.Exists(lockPath)) { problems.Add($"no packages.lock.json for {project}"); continue; }
            locks.Add(new(project, Hex(SkillRegistry.Canonical(File.ReadAllBytes(lockPath)))));
            foreach (JsonProperty tfm in JsonDocument.Parse(File.ReadAllText(lockPath)).RootElement.GetProperty("dependencies").EnumerateObject())
                foreach (JsonProperty dep in tfm.Value.EnumerateObject())
                {
                    if (dep.Value.GetProperty("type").GetString() != "Direct") continue;
                    string resolved = dep.Value.GetProperty("resolved").GetString()!;
                    if (!props.TryGetValue(dep.Name, out string? pinned))
                        problems.Add($"{project}: {dep.Name} {resolved} is not pinned in Directory.Packages.props");
                    else if (!string.Equals(pinned, resolved, StringComparison.OrdinalIgnoreCase))
                        problems.Add($"{project}: lock file resolves {dep.Name} {resolved}, Directory.Packages.props pins {pinned}");
                    direct[dep.Name] = resolved;
                }
        }
        List<PackagePin> packages = [.. direct.Select(kv => new PackagePin(kv.Key, kv.Value, kv.Value.Contains('-')))];

        List<PromptHash> prompts = [];
        foreach (PromptRef p in m.Identity.Prompts)
        {
            if (!promptsBuilt) { prompts.Add(new(p.Id, "not built", 0)); continue; }
            string dll = BuiltAssembly(repo, p.Project);
            string? text = File.Exists(dll) ? ConstString(dll, p.Type, p.Field) : null;
            if (text is null) { problems.Add($"prompt {p.Id}: {p.Type}.{p.Field} not found in {Path.GetFileName(dll)}"); continue; }
            prompts.Add(new(p.Id, Hex(Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\n"))), text.Length));
        }

        List<SkillHash> skills = [];
        foreach (SkillSet set in m.Identity.Skills)
            skills.AddRange(await HashSkillsAsync(Path.Combine(repo, set.Folder), Path.Combine(repo, set.Lock), problems));

        List<DataHash> data = [.. m.Identity.Data.Select(d => HashData(repo, d, problems))];

        return new CandidateIdentity
        {
            Candidate = m.Candidate,
            ManifestSha256 = Hex(SkillRegistry.Canonical(File.ReadAllBytes(manifestPath))),
            GlobalJsonSdk = sdk,
            Packages = packages,
            LockFiles = locks,
            Prompts = prompts,
            Skills = skills,
            Data = data,
            Profile = m.Profile,
            Problems = problems,
        };
    }

    /// <summary>
    /// A skill is identified by its content (ch. 16's hash over every file), not by
    /// the name and version in its front matter: an edit in place keeps both.
    /// </summary>
    public static async Task<List<SkillHash>> HashSkillsAsync(string folder, string lockPath, List<string> problems)
    {
        Dictionary<string, string> locked = File.Exists(lockPath)
            ? JsonDocument.Parse(File.ReadAllText(lockPath)).RootElement.EnumerateArray()
                .ToDictionary(e => e.GetProperty("Name").GetString()!, e => e.GetProperty("Sha256").GetString()!)
            : [];
        List<SkillHash> result = [];
        foreach (Microsoft.Agents.AI.AgentSkill skill in await SkillDiscovery.FromFolderAsync(folder))
        {
            string sha = await SkillRegistry.HashAsync(skill);
            string version = FrontMatterVersion(skill);
            string? lockSha = locked.GetValueOrDefault(skill.Frontmatter.Name);
            bool match = lockSha == sha;
            if (!match) problems.Add($"skill {skill.Frontmatter.Name} {version}: content {sha[..12]} differs from its lock ({(lockSha is null ? "no entry" : lockSha[..12])})");
            result.Add(new(skill.Frontmatter.Name, version, sha, lockSha, match));
        }
        if (result.Count == 0) problems.Add($"no skill found in {folder}");
        return result;
    }

    private static string FrontMatterVersion(Microsoft.Agents.AI.AgentSkill skill) =>
        skill.Frontmatter.Metadata?.GetValueOrDefault("version")?.ToString() ?? "";

    private static DataHash HashData(string repo, DataRef d, List<string> problems)
    {
        string full = Path.Combine(repo, d.Path);
        string[] files = File.Exists(full) ? [full]
            : Directory.Exists(full) ? [.. Directory.GetFiles(full, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)]
            : [];
        if (files.Length == 0) problems.Add($"data {d.Id}: nothing at {d.Path}");
        using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string f in files)
        {
            sha.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(full, f).Replace('\\', '/') + "\0"));
            sha.AppendData(SkillRegistry.Canonical(File.ReadAllBytes(f)));
        }
        return new(d.Id, d.Path, files.Length, Convert.ToHexStringLower(sha.GetHashAndReset()));
    }

    /// <summary>The root projects and every project they reference, repo-relative, '/' separators.</summary>
    public static List<string> Closure(string repo, IEnumerable<string> roots)
    {
        SortedSet<string> seen = new(StringComparer.Ordinal);
        Stack<string> todo = new(roots.Select(r => r.Replace('\\', '/')));
        while (todo.TryPop(out string? p))
        {
            if (!seen.Add(p)) continue;
            string full = Path.Combine(repo, p);
            if (!File.Exists(full)) continue;
            foreach (XElement r in XDocument.Load(full).Descendants("ProjectReference"))
            {
                string target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(full)!, (string)r.Attribute("Include")!));
                todo.Push(Path.GetRelativePath(repo, target).Replace('\\', '/'));
            }
        }
        return [.. seen];
    }

    public static string BuiltAssembly(string repo, string project)
    {
        string full = Path.Combine(repo, project);
        string name = XDocument.Load(full).Descendants("AssemblyName").FirstOrDefault()?.Value ?? Path.GetFileNameWithoutExtension(full);
        return Path.Combine(Path.GetDirectoryName(full)!, "bin", "Release", "net10.0", name + ".dll");
    }

    /// <summary>The value of a const string field, read from metadata: nothing from the assembly is loaded or run.</summary>
    public static string? ConstString(string dll, string typeFullName, string field)
    {
        using FileStream stream = File.OpenRead(dll);
        using PEReader pe = new(stream);
        MetadataReader md = pe.GetMetadataReader();
        foreach (TypeDefinitionHandle th in md.TypeDefinitions)
        {
            TypeDefinition t = md.GetTypeDefinition(th);
            if ($"{md.GetString(t.Namespace)}.{md.GetString(t.Name)}" != typeFullName) continue;
            foreach (FieldDefinitionHandle fh in t.GetFields())
            {
                FieldDefinition f = md.GetFieldDefinition(fh);
                if (md.GetString(f.Name) != field || f.GetDefaultValue().IsNil) continue;
                Constant c = md.GetConstant(f.GetDefaultValue());
                if (c.TypeCode != ConstantTypeCode.String) return null;
                BlobReader blob = md.GetBlobReader(c.Value);
                return blob.ReadUTF16(blob.Length);
            }
        }
        return null;
    }

    public static IEnumerable<string> PrereleaseIds(CandidateIdentity id) => id.Packages.Where(p => p.Prerelease).Select(p => $"{p.Id} {p.Version}");
}
