using System.Diagnostics;
using System.Text.Json;

namespace Ch19.Observability;

/// <summary>A verdict: which step failed, and the span evidence behind it.</summary>
public sealed record Verdict(string Step, string Evidence);

/// <summary>
/// Names the failing step of one request from its exported spans alone. The
/// rules run in pipeline order, so the first broken step wins: a guess made
/// after an empty search is a retrieval problem, not a model problem.
/// </summary>
public static class Diagnosis
{
    public const string Retrieval = "retrieval", Tool = "tool", Model = "model", None = "none", Unknown = "undetermined";

    public static Verdict Diagnose(IReadOnlyList<SpanRecord> spans)
    {
        if (spans.FirstOrDefault(s => s.Tag("gen_ai.operation.name") == "execute_tool"
                && (s.Status == ActivityStatusCode.Error || s.Tag("error.type") is not null)) is { } failedTool)
            return new(Tool, $"{failedTool.Name}: error.type={failedTool.Tag("error.type") ?? "-"}");

        if (spans.FirstOrDefault(s => s.Name == "contoso.retrieval" && s.Tag("contoso.retrieval.hits") == "0") is { } empty)
            return new(Retrieval, $"{empty.Name}: contoso.retrieval.hits=0");

        if (spans.FirstOrDefault(s => s.Name == "contoso.answer.check") is { } check)
            return check.Tag("contoso.answer.grounded") == "False"
                ? new(Model, $"{check.Name}: contoso.answer.reason={check.Tag("contoso.answer.reason")}")
                : new(None, $"{check.Name}: contoso.answer.reason={check.Tag("contoso.answer.reason")}");

        return new(Unknown, "no step span tells the steps apart");
    }

    // One span per line: what a backend export (or a support bundle) would hold.
    public static void Save(string path, IEnumerable<SpanRecord> spans) =>
        File.WriteAllLines(path, spans.Select(s => JsonSerializer.Serialize(s)));

    public static IReadOnlyList<SpanRecord> Load(string path) =>
        [.. File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<SpanRecord>(l)!)];
}
