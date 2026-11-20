using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Ch19.Observability;

/// <summary>
/// The application's own telemetry: one ActivitySource, one Meter, and the rule
/// that every metric tag comes from a short, fixed list. Identifiers such as a
/// conversation or tenant id may go on a span (traces are sampled and access
/// controlled); they never go on a metric, where each new value is a new series.
/// </summary>
public sealed class ContosoTelemetry : IDisposable
{
    public const string SourceName = "Contoso.Assistant";
    public const string MeterName = "Contoso.Assistant";

    // The names the frameworks emit under when no sourceName is passed. Read
    // from a run of MAF 1.28 / Microsoft.Extensions.AI 10.11 (the self-test
    // checks them); the "Experimental." prefix says they may still change.
    public const string AgentFrameworkSource = "Experimental.Microsoft.Agents.AI";
    public const string ExtensionsAISource = "Experimental.Microsoft.Extensions.AI";
    public const string WorkflowSource = "Microsoft.Agents.AI.Workflows";

    /// <summary>Everything a tracer provider must subscribe to, or spans vanish silently.</summary>
    public static readonly string[] Sources = [SourceName, AgentFrameworkSource, ExtensionsAISource, WorkflowSource];

    /// <summary>The meters: ours, plus M.E.AI's gen_ai.client.* instruments.</summary>
    public static readonly string[] Meters = [MeterName, ExtensionsAISource];

    public static readonly string[] Outcomes = ["ok", "no_evidence", "tool_error", "failed"];
    public static readonly string[] Tools = ["get_reimbursement_limit", "search_policy"];

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _requests;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _toolCalls;

    public ContosoTelemetry()
    {
        _requests = _meter.CreateCounter<long>("contoso.assistant.requests", "{request}", "Requests by outcome");
        _duration = _meter.CreateHistogram<double>("contoso.assistant.duration", "s", "End-to-end request time");
        _toolCalls = _meter.CreateCounter<long>("contoso.assistant.tool_calls", "{call}", "Tool calls by tool and result");
    }

    public ActivitySource Source { get; } = new(SourceName);

    /// <summary>Starts the request span. The conversation id is a span attribute only.</summary>
    public Activity? StartRequest(string conversationId)
    {
        Activity? activity = Source.StartActivity("contoso.request");
        activity?.SetTag("contoso.conversation_id", conversationId);
        return activity;
    }

    public void RecordRequest(string outcome, TimeSpan elapsed)
    {
        KeyValuePair<string, object?> tag = new("contoso.outcome", Bound(outcome, Outcomes));
        _requests.Add(1, tag);
        _duration.Record(elapsed.TotalSeconds, tag);
    }

    public void RecordToolCall(string tool, bool failed) =>
        _toolCalls.Add(1,
            new("gen_ai.tool.name", Bound(tool, Tools)),
            new("contoso.result", failed ? "error" : "ok"));

    /// <summary>A value outside the allowed list becomes "other": the series count stays fixed.</summary>
    public static string Bound(string value, string[] allowed) => allowed.Contains(value) ? value : "other";

    public void Dispose()
    {
        _meter.Dispose();
        Source.Dispose();
    }
}

/// <summary>
/// Keeps a failed span's status and error.type but drops its description. At
/// MAF 1.28 a failing tool's exception message lands in the execute_tool span's
/// status description even with sensitive data off, and exception messages can
/// carry whatever the tool was working on. Register it before any exporter.
/// </summary>
public sealed class StatusDescriptionRedactor : OpenTelemetry.BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
    {
        if (activity.Status == ActivityStatusCode.Error && activity.StatusDescription is not null)
            activity.SetStatus(ActivityStatusCode.Error);
    }
}
