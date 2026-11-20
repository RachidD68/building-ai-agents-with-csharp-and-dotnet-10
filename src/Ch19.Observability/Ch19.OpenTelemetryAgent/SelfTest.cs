using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Ch19.Observability;

/// <summary>
/// dotnet run -- --self-test. Offline: a scripted model, a fake provider, and
/// in-memory exporters behind the real OpenTelemetry SDK. Every name and
/// attribute asserted here is one the 1.28 packages actually emitted.
/// </summary>
internal static class SelfTest
{
    private const string EnvSwitch = "OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT";
    private const string Secret = "CONF-7731 Dana Whitfield medical leave";   // seeded confidential text

    private static int s_passed, s_total;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== OpenTelemetry agent: self-test (offline) ===");
        Environment.SetEnvironmentVariable(EnvSwitch, null);

        await EmittedSpansAsync();
        await SubscriptionAsync();
        await SensitiveDataAsync();
        await CardinalityAsync();
        await WorkflowAsync();
        await RetryOwnersAsync();

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    // ---------------------------------------------------------------
    // 1. What MAF 1.28 + M.E.AI 10.11 emit for one agent run with one tool call
    // ---------------------------------------------------------------
    private static async Task EmittedSpansAsync()
    {
        Console.WriteLine("\n-- Spans emitted by one agent run (scripted model, one tool call) --");
        using Capture cap = new(ContosoTelemetry.Sources);
        using ContosoTelemetry telemetry = new();
        ScriptedModel model = new();
        AIAgent agent = ContosoAssistant.Create(model, telemetry, cap.Loggers);
        await ContosoAssistant.AskAsync(agent, telemetry, cap.Logger, "conv-1", "What is the meals limit for a client dinner?");
        cap.Flush();

        IReadOnlyList<SpanRecord> spans = cap.Spans.Spans;
        foreach (SpanRecord s in Ordered(spans))
            Console.WriteLine($"    {Indent(s, spans)}{s.Name}  [{s.Source}]  op={s.Tag("gen_ai.operation.name") ?? "-"}");

        SpanRecord? run = spans.FirstOrDefault(s => s.Tag("gen_ai.operation.name") == "invoke_agent");
        SpanRecord[] chats = [.. spans.Where(s => s.Tag("gen_ai.operation.name") == "chat")];
        SpanRecord? tool = spans.FirstOrDefault(s => s.Tag("gen_ai.operation.name") == "execute_tool");

        Expect($"one invoke_agent span named \"invoke_agent {ContosoAssistant.AgentName}(<gen_ai.agent.id>)\" with gen_ai.agent.name",
            run is not null && run.Name == $"invoke_agent {ContosoAssistant.AgentName}({run.Tag("gen_ai.agent.id")})"
            && run.Tag("gen_ai.agent.name") == ContosoAssistant.AgentName
            && spans.Count(s => s.Tag("gen_ai.operation.name") == "invoke_agent") == 1);
        Expect($"one chat span per model call ({model.Calls} calls, {chats.Length} chat spans), each with token usage",
            chats.Length == model.Calls && chats.All(c => c.Tag("gen_ai.usage.input_tokens") is not null
                && c.Tag("gen_ai.usage.output_tokens") is not null && c.Tag("gen_ai.request.model") == "scripted-model"));
        Expect("one execute_tool span named \"execute_tool get_reimbursement_limit\" with gen_ai.tool.name",
            tool is not null && tool.Name == "execute_tool get_reimbursement_limit"
            && tool.Tag("gen_ai.tool.name") == "get_reimbursement_limit");
        Expect("one trace: chat and execute_tool spans sit under invoke_agent, which sits under contoso.request",
            run is not null && spans.Select(s => s.TraceId).Distinct().Count() == 1
            && chats.Concat([tool!]).All(s => IsUnder(s, run, spans))
            && spans.First(s => s.Name == "contoso.request").SpanId == run.ParentSpanId);

        Console.WriteLine("    attributes as emitted (union over the run):");
        foreach (IGrouping<string, SpanRecord> op in spans.GroupBy(s => s.Tag("gen_ai.operation.name") ?? s.Name))
            Console.WriteLine($"      {op.Key}: {string.Join(", ", op.SelectMany(s => s.Tags.Keys).Distinct().Order())}");
        Console.WriteLine("    metrics as emitted: " + string.Join(", ", cap.Metrics.Points.Select(p => p.Name).Distinct().Order()));
        Console.WriteLine("    gen_ai.client.token.usage tags: " + string.Join(", ",
            cap.Metrics.Points.Where(p => p.Name == "gen_ai.client.token.usage").SelectMany(p => p.Tags.Keys).Distinct().Order()));
    }

    // ---------------------------------------------------------------
    // 2. Subscribing to the right sources (the empty-dashboard failure)
    // ---------------------------------------------------------------
    private static async Task SubscriptionAsync()
    {
        Console.WriteLine("\n-- Which sources the tracer provider must subscribe to --");
        HashSet<string> seen = [];
        using ActivityListener discovery = new()
        {
            ShouldListenTo = s => !s.Name.StartsWith("System.", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (seen) seen.Add(a.Source.Name); },
        };
        ActivitySource.AddActivityListener(discovery);

        async Task<int> ExportedAsync(string[] sources)
        {
            using Capture cap = new(sources);
            using ContosoTelemetry telemetry = new();
            AIAgent agent = ContosoAssistant.Create(new ScriptedModel(), telemetry);
            await ContosoAssistant.AskAsync(agent, telemetry, cap.Logger, "conv-2", "What is the meals limit?");
            await TriageWorkflow.RunAsync("client dinner");
            cap.Flush();
            return cap.Spans.Spans.Count;
        }

        int ownOnly = await ExportedAsync([ContosoTelemetry.SourceName]);
        int all = await ExportedAsync(ContosoTelemetry.Sources);
        Console.WriteLine($"    sources seen by a listener on every non-System source: {string.Join(", ", seen.Order())}");
        Expect($"DEFECT REPRODUCED: subscribed to the app's own source only, {ownOnly} of {all} spans reach the exporter",
            ownOnly == 1 && all > ownOnly);
        Expect("fixed: the four sources in ContosoTelemetry.Sources cover every source that emitted",
            seen.All(ContosoTelemetry.Sources.Contains));
        Expect("the MAF agent and workflow sources are the ones ContosoTelemetry names (no sourceName passed)",
            seen.Contains(ContosoTelemetry.AgentFrameworkSource) && seen.Contains(ContosoTelemetry.WorkflowSource)
            && seen.Contains(ContosoTelemetry.ExtensionsAISource));
    }

    // ---------------------------------------------------------------
    // 3. Sensitive data: prompts and tool results stay out unless switched on
    // ---------------------------------------------------------------
    private static async Task SensitiveDataAsync()
    {
        Console.WriteLine("\n-- Sensitive data in telemetry --");
        string question = $"Employee note: {Secret}. What is the meals limit?";

        int byDefault = await LeaksAsync(question, (t, l) => ContosoAssistant.Create(new ScriptedModel(), t, l));
        Expect("sensitive data off (as shipped): the seeded confidential text appears nowhere in spans, metrics or logs",
            byDefault == 0);

        int switchedOn = await LeaksAsync(question, (t, l) => ContosoAssistant.Create(new ScriptedModel(), t, l, sensitiveData: true));
        Expect($"DEFECT REPRODUCED: EnableSensitiveData = true puts the prompt into telemetry ({switchedOn} exported values carry it)",
            switchedOn > 0);

        int silentNoEnv = await LeaksAsync(question, (t, l) => SilentAgent(new ScriptedModel(), l));
        Expect($"code that never sets the property, {EnvSwitch} unset: nothing leaks ({silentNoEnv} values)",
            silentNoEnv == 0);

        // The variable is read when the process starts using the library, so the
        // check runs in a child process that starts with it set.
        (int silent, int explicitFalse) = await ChildProbeAsync();
        Expect($"DEFECT REPRODUCED: {EnvSwitch}=true and the same code: the prompt leaks ({silent} values)",
            silent > 0);
        Expect($"fixed: EnableSensitiveData = false set in code wins over {EnvSwitch}=true ({explicitFalse} values)",
            explicitFalse == 0);
    }

    /// <summary>Child-process half of the check above: dotnet run -- --sensitive-probe.</summary>
    public static async Task<int> SensitiveProbeAsync()
    {
        string question = $"Employee note: {Secret}. What is the meals limit?";
        int silent = await LeaksAsync(question, (t, l) => SilentAgent(new ScriptedModel(), l));
        int explicitFalse = await LeaksAsync(question, (t, l) => ContosoAssistant.Create(new ScriptedModel(), t, l));
        Console.WriteLine($"silent={silent} explicit={explicitFalse}");
        return 0;
    }

    private static async Task<(int Silent, int Explicit)> ChildProbeAsync()
    {
        string host = Environment.ProcessPath!;
        ProcessStartInfo start = new(host) { RedirectStandardOutput = true, UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(SelfTest).Assembly.Location);
        start.ArgumentList.Add("--sensitive-probe");
        start.Environment[EnvSwitch] = "true";
        using Process child = Process.Start(start)!;
        string output = await child.StandardOutput.ReadToEndAsync();
        await child.WaitForExitAsync();
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(output, @"silent=(\d+) explicit=(\d+)");
        return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : (-1, -1);
    }

    /// <summary>Runs one question and counts exported values that carry the seeded text.</summary>
    private static async Task<int> LeaksAsync(string question, Func<ContosoTelemetry, ILoggerFactory, AIAgent> create)
    {
        using Capture cap = new(ContosoTelemetry.Sources);
        using ContosoTelemetry telemetry = new();
        AIAgent agent = create(telemetry, cap.Loggers);
        await ContosoAssistant.AskAsync(agent, telemetry, cap.Logger, "conv-3", question);
        cap.Flush();
        return TelemetryText.Of(cap.Spans.Spans, cap.Metrics.Points, cap.Logs.Lines)
            .Count(line => line.Contains("CONF-7731", StringComparison.Ordinal));
    }

    /// <summary>The first attempt: telemetry on, the sensitive-data property never written.</summary>
    private static AIAgent SilentAgent(IChatClient model, ILoggerFactory loggers) =>
        new ChatClientAgent(model.AsBuilder().UseOpenTelemetry(loggers).Build(),
                instructions: "Answer reimbursement questions.", name: ContosoAssistant.AgentName,
                tools: [AIFunctionFactory.Create(new PolicyTools(new ContosoTelemetry()).GetReimbursementLimit, "get_reimbursement_limit")])
            .AsBuilder().UseOpenTelemetry().Build();

    // ---------------------------------------------------------------
    // 4. Cardinality: identifiers as metric tags
    // ---------------------------------------------------------------
    private static async Task CardinalityAsync()
    {
        Console.WriteLine("\n-- Cardinality: metric series and span names --");
        const int Conversations = 50;

        async Task<int> DistinctRunNamesAsync(Func<ContosoTelemetry, ILoggerFactory, AIAgent> create)
        {
            using Capture cap = new(ContosoTelemetry.Sources);
            using ContosoTelemetry telemetry = new();
            for (int i = 0; i < 3; i++)   // three instances, as three service replicas would build them
                await ContosoAssistant.AskAsync(create(telemetry, cap.Loggers), telemetry, cap.Logger, $"conv-{i}", "meals limit?");
            cap.Flush();
            return cap.Spans.Spans.Where(s => s.Tag("gen_ai.operation.name") == "invoke_agent").Select(s => s.Name).Distinct().Count();
        }

        int unnamed = await DistinctRunNamesAsync((t, l) => SilentAgent(new ScriptedModel(), l));
        Expect($"DEFECT REPRODUCED: no agent Id, three agent instances give {unnamed} different invoke_agent span names",
            unnamed == 3);
        int fixedId = await DistinctRunNamesAsync((t, l) => ContosoAssistant.Create(new ScriptedModel(), t, l));
        Expect($"fixed: Id = \"{ContosoAssistant.AgentId}\" gives {fixedId} span name for the three instances", fixedId == 1);

        using (Capture cap = new([], ["Naive.Assistant"]))
        using (Meter naive = new("Naive.Assistant"))
        {
            Counter<long> requests = naive.CreateCounter<long>("naive.requests");
            for (int i = 0; i < Conversations; i++)
                requests.Add(1, new("tenant.id", $"tenant-{i % 7}"), new("conversation.id", $"conv-{i}"));
            cap.Flush();
            int series = cap.Metrics.Points.Count(p => p.Name == "naive.requests");
            Expect($"DEFECT REPRODUCED: tenant and conversation ids as tags, {Conversations} requests make {series} series",
                series == Conversations);
        }

        using (Capture cap = new([], [ContosoTelemetry.MeterName]))
        using (ContosoTelemetry telemetry = new())
        {
            string[] outcomes = ["ok", "ok", "no_evidence", "tool_error", "timeout-on-conv-17"];
            for (int i = 0; i < Conversations; i++)
                telemetry.RecordRequest(outcomes[i % outcomes.Length], TimeSpan.FromMilliseconds(10));
            cap.Flush();
            MetricPointRecord[] points = [.. cap.Metrics.Points.Where(p => p.Name == "contoso.assistant.requests")];
            Expect($"fixed: one bounded tag (contoso.outcome), {Conversations} requests make {points.Length} series, " +
                   "an unlisted value counted as \"other\"",
                points.Length <= ContosoTelemetry.Outcomes.Length + 1
                && points.All(p => p.Tags.Keys.SequenceEqual(["contoso.outcome"]))
                && points.Any(p => p.Tags["contoso.outcome"] == "other")
                && points.Sum(p => p.Value) == Conversations);
        }
    }

    // ---------------------------------------------------------------
    // 5. Workflow telemetry at 1.28: invoke_workflow and gen_ai.workflow.name
    // ---------------------------------------------------------------
    private static async Task WorkflowAsync()
    {
        Console.WriteLine("\n-- Workflow spans (WithOpenTelemetry) --");
        using Capture cap = new(ContosoTelemetry.Sources);
        string? output = await TriageWorkflow.RunAsync("client dinner, 120 CAD");
        cap.Flush();
        IReadOnlyList<SpanRecord> spans = cap.Spans.Spans;
        foreach (SpanRecord s in Ordered(spans))
            Console.WriteLine($"    {Indent(s, spans)}{s.Name}  [{string.Join(", ", s.Tags.Keys.Order())}]");

        SpanRecord? run = spans.FirstOrDefault(s => s.Tag("gen_ai.operation.name") == "invoke_workflow");
        Expect($"an invoke_workflow span carries gen_ai.workflow.name = {TriageWorkflow.Name} (output: {output})",
            run is not null && run.Tag("gen_ai.workflow.name") == TriageWorkflow.Name && output == "meals");
        int v1Query = spans.Count(s => s.Tag("workflow.name") == TriageWorkflow.Name);
        Expect($"DEFECT REPRODUCED: a dashboard filtering on the old key workflow.name matches {v1Query} spans",
            v1Query == 0);
    }

    // ---------------------------------------------------------------
    // 6. Retry owners: HTTP pipeline, SDK, application
    // ---------------------------------------------------------------
    private static async Task RetryOwnersAsync()
    {
        Console.WriteLine("\n-- Retry owners (fake provider, no network) --");

        async Task<(int Attempts, int ChatSpans, bool ChatFailed, bool Answered)> RunAsync(
            Func<int, HttpStatusCode> status, bool http, bool sdk, int appAttempts)
        {
            using Capture cap = new(ContosoTelemetry.Sources);
            FakeProvider provider = new(status);
            IChatClient chat = RetryOwners.CreateClient(provider, http, sdk).AsBuilder().UseOpenTelemetry().Build();
            bool answered;
            try { answered = (await RetryOwners.AskWithAppRetriesAsync(chat, appAttempts)).Text.Length > 0; }
            catch (System.ClientModel.ClientResultException) { answered = false; }
            cap.Flush();
            SpanRecord[] chats = [.. cap.Spans.Spans.Where(s => s.Tag("gen_ai.operation.name") == "chat")];
            bool failed = chats.Length > 0 && chats.All(s => s.Status == ActivityStatusCode.Error || s.Tag("error.type") is not null);
            return (provider.Attempts, chats.Length, failed, answered);
        }

        int n = RetryOwners.RetriesPerOwner;
        var stacked = await RunAsync(_ => HttpStatusCode.TooManyRequests, http: true, sdk: true, appAttempts: n);
        Expect($"DEFECT REPRODUCED: three owners against a provider stuck at 429: {stacked.Attempts} HTTP attempts " +
               $"for one question, while the trace shows {stacked.ChatSpans} chat spans",
            stacked.Attempts == (n + 1) * (n + 1) * n && stacked.ChatSpans == n && !stacked.Answered);

        var once = await RunAsync(a => a == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK, http: true, sdk: false, appAttempts: 1);
        Expect($"fixed, one owner (HTTP pipeline), 429 then 200: {once.Attempts} attempts, {once.ChatSpans} chat span, one answer",
            once.Attempts == 2 && once.ChatSpans == 1 && once.Answered);

        var stuck = await RunAsync(_ => HttpStatusCode.TooManyRequests, http: true, sdk: false, appAttempts: 1);
        Expect($"fixed, one owner, provider stuck at 429: {stuck.Attempts} attempts, then one failed chat span",
            stuck.Attempts == n + 1 && stuck.ChatSpans == 1 && stuck.ChatFailed && !stuck.Answered);
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------
    private static bool IsUnder(SpanRecord span, SpanRecord ancestor, IReadOnlyList<SpanRecord> all)
    {
        for (SpanRecord? s = span; s is not null; s = all.FirstOrDefault(p => p.SpanId == s.ParentSpanId))
            if (s.ParentSpanId == ancestor.SpanId) return true;
        return false;
    }

    private static string Indent(SpanRecord span, IReadOnlyList<SpanRecord> all)
    {
        int depth = 0;
        for (SpanRecord? s = all.FirstOrDefault(p => p.SpanId == span.ParentSpanId); s is not null;
             s = all.FirstOrDefault(p => p.SpanId == s.ParentSpanId)) depth++;
        return new string(' ', depth * 2);
    }

    /// <summary>Parents before children (the exporter receives children first, as they end first).</summary>
    private static IEnumerable<SpanRecord> Ordered(IReadOnlyList<SpanRecord> all)
    {
        IEnumerable<SpanRecord> Walk(string parent) =>
            all.Where(s => s.ParentSpanId == parent).SelectMany(s => Walk(s.SpanId).Prepend(s));
        return Walk("").Concat(all.Where(s => s.ParentSpanId != "" && all.All(p => p.SpanId != s.ParentSpanId)));
    }

    private static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }

    /// <summary>A tracer provider, a meter provider and a log capture, torn down together.</summary>
    private sealed class Capture : IDisposable
    {
        private readonly TracerProvider _tracer;
        private readonly MeterProvider _meter;

        public Capture(string[] sources, string[]? meters = null)
        {
            _tracer = Sdk.CreateTracerProviderBuilder()
                .AddSource(sources)
                .AddProcessor(new SimpleActivityExportProcessor(Spans))
                .Build()!;
            _meter = Sdk.CreateMeterProviderBuilder()
                .AddMeter(meters ?? ContosoTelemetry.Meters)
                .AddReader(new BaseExportingMetricReader(Metrics) { TemporalityPreference = MetricReaderTemporalityPreference.Cumulative })
                .Build()!;
            Loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
            Logger = Loggers.CreateLogger("Contoso.Assistant");
        }

        public SpanCapture Spans { get; } = new();
        public MetricCapture Metrics { get; } = new();
        public LogCapture Logs { get; } = new();
        public ILoggerFactory Loggers { get; }
        public ILogger Logger { get; }

        public void Flush()
        {
            _tracer.ForceFlush();
            _meter.ForceFlush();
        }

        public void Dispose()
        {
            _tracer.Dispose();
            _meter.Dispose();
            Loggers.Dispose();
        }
    }
}
