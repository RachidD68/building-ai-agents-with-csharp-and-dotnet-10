// ============================================================================
// Chapter 13 — Parallel Analysis: idiomatic fan-out / fan-in
// ----------------------------------------------------------------------------
// A dispatcher fans a product review out to three analyzer agents that run
// concurrently — sentiment, topic, and a one-line summary. A fan-in barrier
// edge holds their results until all three have produced output, then streams
// them to an aggregator that assembles the final report.
//
//   Dispatcher ──┬─► SentimentAnalyzer ─┐
//                ├─► TopicAnalyzer ──────┼─► (barrier) ─► Aggregator ─► output
//                └─► SummaryAnalyzer ────┘
//
// AddFanOutEdge wires one source to many targets; the runtime schedules the
// targets in the same superstep, so they run in parallel. AddFanInBarrierEdge
// holds messages until every source has produced at least one, then delivers
// them to the aggregator. The aggregator collects the three results and yields
// the workflow output.
//
// Note: the barrier delivers branch results one message at a time (not as a
// single list), so the aggregator counts until it has all three before it
// yields. Runs against Ollama (llama3.2) by default.
// ============================================================================

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

HostApplicationBuilder hostBuilder = Host.CreateApplicationBuilder(args);
hostBuilder.Services.AddLlmChatClient(hostBuilder.Configuration);

using IHost host = hostBuilder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Chapter 13: Parallel Analysis (fan-out / fan-in) ===\n");

string review =
    "I bought this espresso machine three weeks ago. The build quality is " +
    "excellent and the coffee tastes great, but the water tank is tiny and " +
    "the instructions were almost useless. Customer support did reply quickly " +
    "when I emailed them, which I appreciated.";

// ---------------------------------------------------------------
// Three analyzer agents. Each gets its own narrow instruction.
// ---------------------------------------------------------------
AIAgent sentimentAgent = new ChatClientAgent(
    chatClient,
    instructions: "Classify the overall sentiment of the product review as " +
                  "Positive, Negative, or Mixed, then give a one-sentence reason. " +
                  "Answer in one line.",
    name: "Sentiment");

AIAgent topicAgent = new ChatClientAgent(
    chatClient,
    instructions: "List the product aspects the review mentions (e.g. build " +
                  "quality, price, support) as a short comma-separated list. " +
                  "Answer in one line.",
    name: "Topics");

AIAgent summaryAgent = new ChatClientAgent(
    chatClient,
    instructions: "Summarize the product review in a single short sentence.",
    name: "Summary");

// ---------------------------------------------------------------
// The dispatcher forwards the review text to all three analyzers.
// Each analyzer is wrapped as a function executor that runs its
// agent and tags the result so the aggregator can label it.
// ---------------------------------------------------------------
ExecutorBinding dispatcher =
    BuildAgentNode("dispatch", null, passthrough: true);

ExecutorBinding sentimentNode = BuildAgentNode("sentiment", sentimentAgent);
ExecutorBinding topicNode = BuildAgentNode("topics", topicAgent);
ExecutorBinding summaryNode = BuildAgentNode("summary", summaryAgent);

ExecutorBinding aggregator = new ReportAggregator(expected: 3).BindExecutor();

Workflow workflow = new WorkflowBuilder(dispatcher)
    .AddFanOutEdge(dispatcher, [sentimentNode, topicNode, summaryNode])
    .AddFanInBarrierEdge([sentimentNode, topicNode, summaryNode], aggregator)
    .WithOutputFrom(aggregator)
    .WithName("ParallelReviewAnalysis")
    .Build();

// ---------------------------------------------------------------
// Print the Mermaid graph at startup so the run also demonstrates
// visualization. Paste this into any Mermaid renderer to see the
// fan-out / fan-in topology.
// ---------------------------------------------------------------
Console.WriteLine("--- Workflow graph (Mermaid) ---");
Console.WriteLine(WorkflowVisualizer.ToMermaidString(workflow));
Console.WriteLine();

Console.WriteLine($"Review:\n{review}\n");
Console.WriteLine("Running three analyzers in parallel...\n");

Run run = await InProcessExecution.RunAsync(
    workflow, review, sessionId: Guid.NewGuid().ToString(), cts.Token);

foreach (WorkflowEvent evt in run.OutgoingEvents)
{
    if (evt is WorkflowOutputEvent output)
    {
        Console.WriteLine(output.Data);
    }
}

Console.WriteLine("\n=== Parallel analysis demo complete! ===");

// ---------------------------------------------------------------
// Helper: wrap an agent (or a passthrough) as a tagged function node.
// ---------------------------------------------------------------
ExecutorBinding BuildAgentNode(string id, AIAgent? agent, bool passthrough = false)
{
    if (passthrough)
    {
        // The dispatcher just forwards the review to every analyzer.
        return ExecutorBindingExtensions.BindAsExecutor<string, string>(
            (input, context, ct) => ValueTask.FromResult(input), id: id);
    }

    return ExecutorBindingExtensions.BindAsExecutor<string, AnalysisResult>(
        async (input, context, ct) =>
        {
            AgentSession session = await agent!.CreateSessionAsync(ct);
            AgentResponse response = await agent.RunAsync(input, session, cancellationToken: ct);
            Console.WriteLine($"  [{id}] done");
            return new AnalysisResult(agent.Name ?? id, response.Text.Trim());
        },
        id: id);
}

// ---------------------------------------------------------------
// Types
// ---------------------------------------------------------------

/// <summary>One analyzer's labelled finding.</summary>
internal sealed record AnalysisResult(string Label, string Finding);

/// <summary>
/// Collects the analyzer results delivered by the fan-in barrier and yields a
/// combined report once all expected branches have reported. The barrier
/// delivers one message per branch, so this executor counts.
/// </summary>
[YieldsOutput(typeof(string))]
internal sealed class ReportAggregator(int expected) : Executor<AnalysisResult>("aggregator")
{
    private readonly List<AnalysisResult> _results = new();
    private readonly int _expected = expected;

    public override async ValueTask HandleAsync(
        AnalysisResult result, IWorkflowContext context, CancellationToken ct)
    {
        _results.Add(result);
        if (_results.Count < _expected)
        {
            return;
        }

        System.Text.StringBuilder report = new();
        report.AppendLine("=== Analysis Report ===");
        foreach (AnalysisResult r in _results.OrderBy(r => r.Label))
        {
            report.AppendLine($"{r.Label,-10}: {r.Finding}");
        }
        await context.YieldOutputAsync(report.ToString().TrimEnd(), ct);
    }
}
