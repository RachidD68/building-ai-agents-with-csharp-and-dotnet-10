// ============================================================================
// Chapter 12 — Concurrent orchestration (MAF 1.11)
// ----------------------------------------------------------------------------
// The built-in Concurrent orchestration fans one input out to N specialists
// that run in PARALLEL, then aggregates their results. The runtime does the
// Task.WhenAll for you — you supply the agents and an aggregator that decides
// how to combine the parallel responses into one final message.
//
// Here three reviewers analyse the same design doc at once:
//   - PerformanceReviewer: latency, throughput, scaling
//   - SecurityReviewer:    auth, data exposure, attack surface
//   - MaintainabilityReviewer: coupling, testability, operability
// The aggregator merges their three assessments into a single labelled report.
//
// AgentWorkflowBuilder.BuildConcurrent(agents, aggregator) returns a Workflow,
// which we run through InProcessExecution like any other workflow.
// ============================================================================

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Chapter 11: Concurrent orchestration ===");
Console.WriteLine();

// ── Three reviewers, each with a narrow lens ────────────────────────
ChatClientAgent performanceReviewer = new(
    chatClient,
    instructions: """
        You are a performance engineer. Review the design for latency,
        throughput, and scaling risks. Reply with 2-3 short bullet points.
        No preamble.
        """,
    name: "PerformanceReviewer",
    description: "Reviews designs for performance and scaling risks.");

ChatClientAgent securityReviewer = new(
    chatClient,
    instructions: """
        You are a security engineer. Review the design for authentication,
        data exposure, and attack-surface risks. Reply with 2-3 short bullet
        points. No preamble.
        """,
    name: "SecurityReviewer",
    description: "Reviews designs for security risks.");

ChatClientAgent maintainabilityReviewer = new(
    chatClient,
    instructions: """
        You are a staff engineer focused on maintainability. Review the design
        for coupling, testability, and operability. Reply with 2-3 short bullet
        points. No preamble.
        """,
    name: "MaintainabilityReviewer",
    description: "Reviews designs for maintainability risks.");

// ── The aggregator: merge → label each reviewer's findings ──────────
// BuildConcurrent hands the aggregator the list of each agent's output
// (one List<ChatMessage> per agent). We stitch them into a single merged
// report, attributing each block to the reviewer who produced it. This is
// the "merge" aggregation strategy; vote / rank / pick-best are alternatives.
static List<ChatMessage> MergeReviews(IList<List<ChatMessage>> perAgentResults)
{
    System.Text.StringBuilder report = new();
    report.AppendLine("MERGED DESIGN REVIEW");
    report.AppendLine("====================");

    foreach (List<ChatMessage> agentMessages in perAgentResults)
    {
        foreach (ChatMessage message in agentMessages)
        {
            string author = string.IsNullOrWhiteSpace(message.AuthorName)
                ? "Reviewer"
                : message.AuthorName;

            // Some providers prefix the assistant turn with a bare role token
            // ("assistant\n\n..."). Trim it so the merged report reads cleanly.
            string body = message.Text.TrimStart();
            if (body.StartsWith("assistant", StringComparison.OrdinalIgnoreCase))
            {
                body = body["assistant".Length..].TrimStart();
            }

            report.AppendLine();
            report.AppendLine($"## {author}");
            report.AppendLine(body);
        }
    }

    return [new ChatMessage(ChatRole.Assistant, report.ToString())];
}

// ── Build the concurrent workflow ───────────────────────────────────
Workflow workflow = AgentWorkflowBuilder.BuildConcurrent(
    [performanceReviewer, securityReviewer, maintainabilityReviewer],
    MergeReviews);

// ── Run it: one input, three agents in parallel, one merged output ──
const string designDoc =
    "Design: a public REST endpoint that accepts an uploaded CSV, parses it " +
    "synchronously on the request thread, writes every row to a SQL table in a " +
    "loop, and returns 200 when done. No auth. No size limit. One shared DB " +
    "connection for the whole process.";

Console.WriteLine("Design under review:");
Console.WriteLine(designDoc);
Console.WriteLine();
Console.WriteLine("Running three reviewers in parallel...");
Console.WriteLine();

Run run = await InProcessExecution.Default.RunAsync(
    workflow, designDoc,
    sessionId: Guid.NewGuid().ToString(),
    cts.Token);

// The aggregated result is the single WorkflowOutputEvent carrying the
// List<ChatMessage> the aggregator returned. (Streaming token updates arrive
// as AgentResponseUpdateEvent, a subtype of WorkflowOutputEvent, so we match
// on the payload type to pick out the final merged report.)
foreach (WorkflowEvent evt in run.OutgoingEvents)
{
    if (evt is WorkflowOutputEvent { Data: List<ChatMessage> merged })
    {
        foreach (ChatMessage message in merged)
        {
            Console.WriteLine(message.Text);
        }
    }
}

Console.WriteLine();
Console.WriteLine("=== Done ===");
