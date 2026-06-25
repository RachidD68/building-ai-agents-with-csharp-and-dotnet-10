// ============================================================================
// Chapter 12 — Sub-workflows and workflows-as-agents (composition)
// ----------------------------------------------------------------------------
// Once a workflow exists you can compose it two ways:
//
//   1. As a node inside a bigger workflow — innerWorkflow.BindAsExecutor("id").
//      The inner graph becomes a single executor in the parent; its typed
//      input and output are preserved. This is how you build libraries of
//      reusable sub-pipelines.
//
//   2. As an agent — innerWorkflow.AsAIAgent(). The whole workflow now looks
//      like any other AIAgent: you can host it, hand it to another workflow,
//      or call it directly. (The workflow's entry executor must speak the
//      chat protocol — i.e. accept chat messages — which agent-based
//      workflows do; that is why the inner workflow here is an agent pipeline.)
//
// Part 1 is fully deterministic so its output is stable. Part 2 runs against
// Ollama (llama3.2) by default; see appsettings.json.
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

Console.WriteLine("=== Chapter 12: Sub-workflows & workflows-as-agents ===\n");

// ---------------------------------------------------------------
// Part 1: embed an inner workflow as a node in a parent workflow.
// ---------------------------------------------------------------
Console.WriteLine("--- Part 1: inner workflow as a sub-workflow node ---\n");

// Inner pipeline: normalize whitespace -> count words -> emit a summary.
static Workflow BuildTextStats()
{
    ExecutorBinding normalize = new NormalizeExecutor().BindExecutor();
    ExecutorBinding wordCount = new WordCountExecutor().BindExecutor();
    return new WorkflowBuilder(normalize)
        .AddEdge(normalize, wordCount)
        .WithOutputFrom(wordCount)
        .WithName("TextStats")
        .Build();
}

// Parent: run the inner pipeline, then wrap its output in a report header.
ExecutorBinding statsNode = BuildTextStats().BindAsExecutor("text-stats");
ExecutorBinding reportNode = new ReportExecutor().BindExecutor();

Workflow parent = new WorkflowBuilder(statsNode)
    .AddEdge(statsNode, reportNode)
    .WithOutputFrom(reportNode)
    .WithName("ParentReport")
    .Build();

string messy = "   the   quick brown   fox   jumps over    the lazy dog   ";
Console.WriteLine($"Input: \"{messy}\"");

Run parentRun = await InProcessExecution.RunAsync(
    parent, messy, sessionId: Guid.NewGuid().ToString(), cts.Token);

foreach (WorkflowEvent evt in parentRun.OutgoingEvents)
{
    if (evt is WorkflowOutputEvent output)
    {
        Console.WriteLine($"Output: {output.Data}");
    }
}

// ---------------------------------------------------------------
// Part 2: expose an agent-based workflow as a single AIAgent.
// ---------------------------------------------------------------
Console.WriteLine("\n--- Part 2: inner workflow exposed via AsAIAgent() ---\n");

AIAgent researcher = new ChatClientAgent(
    chatClient,
    instructions: "You are a researcher. In two sentences, state the key idea " +
                  "behind the topic you are given.",
    name: "Researcher");

AIAgent summarizer = new ChatClientAgent(
    chatClient,
    instructions: "Rewrite the text you receive as a single punchy sentence.",
    name: "Summarizer");

// A Researcher -> Summarizer pipeline, treated as one agent.
Workflow researchPipeline = AgentWorkflowBuilder.BuildSequential(researcher, summarizer);
AIAgent pipelineAsAgent = researchPipeline.AsAIAgent();

Console.WriteLine("Calling the two-agent workflow as if it were a single agent...\n");

AgentSession session = await pipelineAsAgent.CreateSessionAsync(cts.Token);
AgentResponse agentResult = await pipelineAsAgent.RunAsync(
    "the role of backpressure in streaming systems", session, cancellationToken: cts.Token);

Console.WriteLine(agentResult.Text.Trim());

Console.WriteLine("\n=== Sub-workflow demo complete! ===");

// ---------------------------------------------------------------
// Inner-pipeline executors (deterministic).
// ---------------------------------------------------------------

/// <summary>Collapses runs of whitespace and trims.</summary>
internal sealed class NormalizeExecutor() : Executor<string, string>("normalize")
{
    public override ValueTask<string> HandleAsync(
        string text, IWorkflowContext context, CancellationToken ct)
        => ValueTask.FromResult(
            System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " "));
}

/// <summary>Counts words and yields a summary string as the inner workflow's output.</summary>
[YieldsOutput(typeof(string))]
internal sealed class WordCountExecutor() : Executor<string>("word-count")
{
    public override async ValueTask HandleAsync(
        string text, IWorkflowContext context, CancellationToken ct)
    {
        int words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        await context.YieldOutputAsync($"{words} words | \"{text}\"", ct);
    }
}

/// <summary>Wraps the inner workflow's output in a report header.</summary>
[YieldsOutput(typeof(string))]
internal sealed class ReportExecutor() : Executor<string>("report")
{
    public override async ValueTask HandleAsync(
        string summary, IWorkflowContext context, CancellationToken ct)
        => await context.YieldOutputAsync($"=== TEXT REPORT ===\n{summary}", ct);
}
