// ============================================================================
// Exercise 02: Traced Pipeline (intermediate)
// ----------------------------------------------------------------------------
// The multi-agent chapter's sequential pipeline (Researcher -> Writer ->
// Editor), one span per stage: pipeline.researcher, pipeline.writer and
// pipeline.editor are siblings under one pipeline.run span. Each stage span
// carries input.length and output.length, and no span carries any text.
//
// The first edition's solution also tagged pipeline.run with pipeline.topic,
// the user's topic verbatim: user text in a span attribute. The self-test
// reproduces that before checking the fix.
//
//   dotnet run                    one topic through the configured model, console exporter
//   dotnet run -- --self-test     offline: scripted stages, the real TracerProvider
// ============================================================================

using System.Diagnostics;
using AgenticAI.Common.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Trace;

if (args.Contains("--self-test")) return await SelfTest.RunAsync();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddOpenTelemetry().WithTracing(t => t.AddSource(TracedPipeline.SourceName).AddConsoleExporter());
builder.Services.AddLlmChatClient(builder.Configuration);
using IHost host = builder.Build();
await host.StartAsync();

Console.WriteLine("=== Traced pipeline: Researcher -> Writer -> Editor ===\n");
const string Topic = "How OpenTelemetry tracing helps debug multi-agent pipelines";
using TracedPipeline pipeline = new(host.Services.GetRequiredService<IChatClient>());
string result = await pipeline.RunAsync(Topic);
Console.WriteLine($"Topic: {Topic}\n\n{result}\n");
await host.StopAsync();   // the console exporter prints the four spans below
return 0;

/// <summary>Three agents in sequence, each stage in its own span under one run span.</summary>
public sealed class TracedPipeline(IChatClient chat) : IDisposable
{
    public const string SourceName = "Contoso.TracedPipeline";

    public static readonly (string Stage, string Instructions)[] Stages =
    [
        ("researcher", "List 4-5 key facts about the topic as short bullet points. Return only the bullets."),
        ("writer", "Turn the research notes into one clear paragraph. Use only the notes."),
        ("editor", "Polish the draft for clarity and concision. Return only the edited text."),
    ];

    private readonly ActivitySource _source = new(SourceName);

    public async Task<string> RunAsync(string topic, CancellationToken ct = default)
    {
        using Activity? run = _source.StartActivity("pipeline.run");
        run?.SetTag("input.length", topic.Length);
        string text = topic;
        foreach ((string stage, string instructions) in Stages)
        {
            // Started while pipeline.run is Activity.Current, ended before the next
            // stage starts: three siblings, not a chain.
            using Activity? span = _source.StartActivity($"pipeline.{stage}");
            span?.SetTag("input.length", text.Length);
            ChatClientAgent agent = new(chat, instructions, name: stage);
            text = (await agent.RunAsync(text, await agent.CreateSessionAsync(ct), cancellationToken: ct)).Text;
            span?.SetTag("output.length", text.Length);
        }
        run?.SetTag("output.length", text.Length);
        return text;
    }

    public void Dispose() => _source.Dispose();
}

/// <summary>The offline check: dotnet run -- --self-test.</summary>
internal static class SelfTest
{
    private const string Topic = "Contoso travel policy for client dinners in Toronto";
    private static int s_passed, s_total;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Traced pipeline: self-test (offline) ===\n");
        StageModel model = new();

        // The first edition's run span: the same structure plus the topic itself as a tag.
        List<Activity> v1 = await TraceAsync(TracedPipeline.SourceName, async () =>
        {
            using ActivitySource source = new(TracedPipeline.SourceName);
            using Activity? run = source.StartActivity("pipeline.run");
            run?.SetTag("pipeline.topic", Topic);
            await Task.CompletedTask;
        });
        Expect("DEFECT REPRODUCED: the first edition's pipeline.run carries pipeline.topic = the user's topic, verbatim",
            v1.Single().GetTagItem("pipeline.topic") as string == Topic);

        string output = "";
        List<Activity> spans = await TraceAsync(TracedPipeline.SourceName, async () =>
        {
            using TracedPipeline pipeline = new(model);
            output = await pipeline.RunAsync(Topic);
        });

        Activity? run = spans.SingleOrDefault(a => a.OperationName == "pipeline.run");
        Activity[] stages = [.. spans.Where(a => a.OperationName != "pipeline.run").OrderBy(a => a.StartTimeUtc)];
        foreach (Activity a in spans.OrderBy(a => a.StartTimeUtc))
            Console.WriteLine($"    {(a == run ? "" : "  ")}{a.OperationName,-20} parent={(a.ParentSpanId == default ? "-" : a.ParentSpanId.ToHexString())} " +
                              string.Join(" ", a.TagObjects.Select(t => $"{t.Key}={t.Value}")));

        Expect("three stage spans, in order: pipeline.researcher, pipeline.writer, pipeline.editor",
            stages.Select(s => s.OperationName).SequenceEqual(["pipeline.researcher", "pipeline.writer", "pipeline.editor"]));
        Expect("all three are siblings: the same parent, pipeline.run, and the same trace",
            run is not null && stages.All(s => s.ParentSpanId == run.SpanId && s.TraceId == run.TraceId));
        Expect("stages do not overlap: each ends before the next starts",
            stages.Zip(stages.Skip(1)).All(p => p.First.StartTimeUtc + p.First.Duration <= p.Second.StartTimeUtc));

        string[] texts = [Topic, .. model.Outputs];
        bool lengthsMatch = stages.Select((s, i) =>
            (int?)s.GetTagItem("input.length") == texts[i].Length && (int?)s.GetTagItem("output.length") == texts[i + 1].Length).All(ok => ok);
        Expect("each stage's input.length and output.length match the text it received and produced", lengthsMatch);

        bool noText = spans.SelectMany(a => a.TagObjects).All(t => t.Value is int)
                      && !spans.SelectMany(a => a.TagObjects).Any(t => texts.Any(x => t.Value?.ToString() == x));
        Expect("no span carries text: every tag is a length", noText && output == model.Outputs[^1]);

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    /// <summary>Runs code under the real TracerProvider and returns the spans an exporter received.</summary>
    private static async Task<List<Activity>> TraceAsync(string source, Func<Task> body)
    {
        List<Activity> exported = [];
        using (TracerProvider provider = Sdk.CreateTracerProviderBuilder()
                   .AddSource(source)
                   .AddProcessor(new SimpleActivityExportProcessor(new ListExporter(exported)))
                   .Build()!)
        {
            await body();
            provider.ForceFlush();
        }
        return exported;
    }

    private static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}

/// <summary>Keeps every exported span.</summary>
internal sealed class ListExporter(List<Activity> sink) : BaseExporter<Activity>
{
    public override ExportResult Export(in Batch<Activity> batch)
    {
        foreach (Activity a in batch) sink.Add(a);
        return ExportResult.Success;
    }
}

/// <summary>A scripted model: each stage returns a fixed text, so lengths are known.</summary>
internal sealed class StageModel : IChatClient
{
    private static readonly string[] Replies =
    [
        "- Client dinners are client_meals\n- The limit is per person\n- Receipts are required",
        "Client dinners count as client meals, with a per-person limit and a receipt for every claim.",
        "Client dinners are client meals: a per-person limit, and every claim needs a receipt.",
    ];

    public List<string> Outputs { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string reply = Replies[Outputs.Count % Replies.Length];
        Outputs.Add(reply);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
