using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ch23.QualificationRunner;

/// <summary>
/// The candidate manifest: what is qualified (projects, prompts, skills, data,
/// model profile), how (layers of steps, each with its counting rule) and the
/// acceptance matrix. Written by hand, frozen before the campaign, no secrets.
/// </summary>
public sealed record CandidateManifest
{
    public required string Candidate { get; init; }
    public string Description { get; init; } = "";
    public required ModelProfile Profile { get; init; }
    public required IdentityInputs Identity { get; init; }
    public required List<LayerSpec> Layers { get; init; }
    public required List<MatrixRow> Matrix { get; init; }
    public List<string> Limits { get; init; } = [];

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static CandidateManifest Parse(string json) =>
        JsonSerializer.Deserialize<CandidateManifest>(json, Json) ?? throw new InvalidDataException("empty manifest");
}

/// <summary>The configuration profile: provider and model names, never a key.</summary>
public sealed record ModelProfile(string Provider, string ChatModel, string? EmbeddingModel, string Endpoint, string ConfigurationProfile);

public sealed record IdentityInputs
{
    /// <summary>Root projects; the runner follows their ProjectReference items for the lock files.</summary>
    public required List<string> Projects { get; init; }
    public List<PromptRef> Prompts { get; init; } = [];
    public List<SkillSet> Skills { get; init; } = [];
    public List<DataRef> Data { get; init; } = [];
}

/// <summary>A prompt as compiled: a const string field of a built assembly.</summary>
public sealed record PromptRef(string Id, string Project, string Type, string Field);

public sealed record SkillSet(string Folder, string Lock);

public sealed record DataRef(string Id, string Path);

public sealed record LayerSpec
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    /// <summary>"offline" layers run with --offline; "real" layers need --real and a budget.</summary>
    public required string Mode { get; init; }
    public required string Proves { get; init; }
    public required string DoesNotProve { get; init; }
    /// <summary>"restore-build": one locked restore and one -warnaserror build per identity project.</summary>
    public string? Generate { get; init; }
    public List<StepSpec> Steps { get; init; } = [];
}

public sealed record StepSpec
{
    public required string Id { get; init; }
    /// <summary>restore | build | run | probe</summary>
    public required string Kind { get; init; }
    public string? Project { get; init; }
    public List<string> Args { get; init; } = [];
    /// <summary>none | fake | ollama | azure: where the answers came from.</summary>
    public string Provider { get; init; } = "none";
    public CountRule Count { get; init; } = CountRule.Result;
    public Dictionary<string, string> Env { get; init; } = [];
    /// <summary>Environment variables that must be set, or the step is not run (never counted as a pass).</summary>
    public List<string> RequiresEnv { get; init; } = [];
    public int MaxSeconds { get; init; } = 600;
    /// <summary>Exit codes that still count as "the run completed" for a provider step (a failing eval exits 1).</summary>
    public List<int> CompletedExitCodes { get; init; } = [0];
}

/// <summary>
/// How a step's cases are counted from its output. result: every "RESULT a/b" line,
/// summed. ratio: a regex with groups pass and total, summed over all matches.
/// tally: one regex for a passing line, one for a failing line.
/// </summary>
public sealed record CountRule(string Kind, string? Pattern = null, string? Pass = null, string? Fail = null)
{
    public static readonly CountRule Result = new("result", @"^RESULT (?<pass>\d+)/(?<total>\d+)\s*$");

    public (int Passed, int Total) Apply(string output)
    {
        RegexOptions o = RegexOptions.Multiline | RegexOptions.CultureInvariant;
        switch (Kind)
        {
            case "result":
            case "ratio":
                int p = 0, t = 0;
                foreach (Match m in Regex.Matches(output, Pattern ?? Result.Pattern!, o))
                {
                    p += int.Parse(m.Groups["pass"].Value, System.Globalization.CultureInfo.InvariantCulture);
                    t += int.Parse(m.Groups["total"].Value, System.Globalization.CultureInfo.InvariantCulture);
                }
                return (p, t);
            case "tally":
                int ok = Regex.Matches(output, Pass!, o).Count, bad = Regex.Matches(output, Fail!, o).Count;
                return (ok, ok + bad);
            default:
                throw new InvalidDataException($"unknown count rule '{Kind}'");
        }
    }
}

/// <summary>
/// One promise of the acceptance matrix: the test that would fail if the promise
/// were broken (a step and a PASS line, or a ratio with a threshold declared here,
/// before the run), the data it runs on, what is expected, and the kind of evidence.
/// </summary>
public sealed record MatrixRow
{
    public required string Id { get; init; }
    public required string Promise { get; init; }
    /// <summary>contract | integration | provider | workload | human</summary>
    public required string Evidence { get; init; }
    public string? Step { get; init; }
    public string? Check { get; init; }
    public string? Ratio { get; init; }
    public double Threshold { get; init; } = 1.0;
    public required string Data { get; init; }
    public required string Expected { get; init; }
}

/// <summary>Refuses a manifest that carries anything that looks like a secret.</summary>
public static partial class SecretScan
{
    [GeneratedRegex(@"(?i)(api[-_]?key|secret|password|pwd|token|connection[-_]?string|credential|accountkey)")]
    private static partial Regex SecretName();

    [GeneratedRegex(@"(?i)(sk-[a-z0-9_-]{16,}|eyJ[a-z0-9_-]{10,}\.[a-z0-9_-]{10,}|AccountKey=|SharedAccessSignature|[?&](api-key|key|sig|code|token)=[^&\s]+|-----BEGIN [A-Z ]*PRIVATE KEY)")]
    private static partial Regex SecretValue();

    public static List<string> Find(string json)
    {
        List<string> found = [];
        Walk(JsonNode.Parse(json), "$", found);
        return found;
    }

    private static void Walk(JsonNode? node, string path, List<string> found)
    {
        switch (node)
        {
            case JsonObject o:
                foreach ((string name, JsonNode? child) in o)
                {
                    string p = $"{path}.{name}";
                    if (SecretName().IsMatch(name) && child is JsonValue v && v.TryGetValue(out string? s) && s.Length > 0)
                        found.Add($"{p}: a value under a secret-like name");
                    Walk(child, p, found);
                }
                break;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++) Walk(a[i], $"{path}[{i}]", found);
                break;
            case JsonValue value when value.TryGetValue(out string? text) && SecretValue().IsMatch(text):
                found.Add($"{path}: a value that looks like a key or a signed URL");
                break;
        }
    }
}
