using System.Diagnostics;
using System.Text.Json;

namespace Ch19.Observability;

/// <summary>A verdict: which step failed, and the span evidence behind it.</summary>
public sealed record Verdict(string Step, string Evidence);

/// <summary>
/// Names the failing step of one request from its exported spans alone.
/// Unambiguous errors come first (a failed tool), then retrieval, then the
/// answer check: a guess made after an empty search is a retrieval problem,
/// not a model problem. The answer check is read by its reason code, never by
/// how an exporter spelled the boolean.
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
        {
            // The reason code decides: the spellings "False" and "false" are one verdict, and only
            // matches_tool means the answer is grounded. A check without a reason proves nothing.
            string? reason = check.Tag("contoso.answer.reason");
            string step = reason is null ? Unknown : reason == "matches_tool" ? None : Model;
            return new(step, $"{check.Name}: contoso.answer.reason={reason ?? "-"}");
        }

        return new(Unknown, "no step span tells the steps apart");
    }

    // One span per line, in this runner's own SpanRecord format. An export from a
    // backend (OTLP/JSON, a vendor's trace download) has to be converted to it first.
    public static void Save(string path, IEnumerable<SpanRecord> spans) =>
        File.WriteAllLines(path, spans.Select(s => JsonSerializer.Serialize(s)));

    public static IReadOnlyList<SpanRecord> Load(string path) =>
        [.. File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<SpanRecord>(l)!)];
}
