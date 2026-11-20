using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ch21.EvidenceEval;

/// <summary>A tool call the case requires, with the arguments that must match.</summary>
public sealed record RequiredCall(string Name, IReadOnlyDictionary<string, string> Arguments);

/// <summary>
/// One graded case. <see cref="Issued"/> is what the request was allowed to see
/// (evidence id to text); <see cref="Supporting"/> names the issued ids that hold
/// the answer. A case with no issued evidence must end in an abstention.
/// </summary>
public sealed record EvidenceCase(
    string Id,
    string Slice,
    string Question,
    int? ExpectedDays,
    IReadOnlyDictionary<string, string> Issued,
    IReadOnlyList<string> Supporting,
    bool ExpectAbstain,
    IReadOnlyList<string> ForbiddenTools,
    IReadOnlyList<RequiredCall> RequiredTools)
{
    /// <summary>The user message for a live run: the issued evidence, then the question.</summary>
    public string Prompt() => Issued.Count == 0
        ? $"Evidence you may use: (none)\n\nQuestion: {Question}"
        : $"Evidence you may use:\n{string.Join("\n", Issued.Select(e => $"[{e.Key}] {e.Value}"))}\n\nQuestion: {Question}";
}

/// <summary>A versioned set of cases, looked up by the query an EvalItem carries.</summary>
public sealed class CaseBook
{
    private readonly Dictionary<string, EvidenceCase> _byQuery = new(StringComparer.Ordinal);

    public CaseBook(string version, IEnumerable<EvidenceCase> cases)
    {
        Version = version;
        Cases = [.. cases];
        foreach (EvidenceCase c in Cases)
        {
            _byQuery.Add(c.Question, c);   // a duplicate question is a broken set: throw
            _byQuery.TryAdd(c.Prompt(), c);
        }
    }

    public string Version { get; }
    public IReadOnlyList<EvidenceCase> Cases { get; }

    public EvidenceCase For(string query) => _byQuery.TryGetValue(query, out EvidenceCase? c)
        ? c : throw new KeyNotFoundException($"No case for query '{query}'.");

    public static CaseBook Load(string path)
    {
        CaseFile file = JsonSerializer.Deserialize<CaseFile>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException($"{path} is empty.");
        return new CaseBook(file.Version, file.Cases.Select(c => new EvidenceCase(
            c.Id, c.Slice, c.Question, c.ExpectedDays, c.Issued ?? [], c.Supporting ?? [], c.ExpectAbstain,
            c.ForbiddenTools ?? [], c.RequiredTools ?? [])));
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private sealed record CaseFile(string Version, List<CaseRow> Cases);

    private sealed record CaseRow(
        string Id, string Slice, string Question, int? ExpectedDays,
        Dictionary<string, string>? Issued, List<string>? Supporting, bool ExpectAbstain,
        List<string>? ForbiddenTools, [property: JsonPropertyName("requiredTools")] List<RequiredCall>? RequiredTools);
}
