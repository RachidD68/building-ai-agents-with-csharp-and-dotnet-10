// ============================================================================
// Chapter 12 — Agents as Workflow Executors
// ----------------------------------------------------------------------------
// Earlier samples wired plain string-transform executors into a graph. The
// real payoff of workflows in an agent framework is orchestrating *agents*.
// When you hand an AIAgent to the workflow builder it is wrapped in an
// agent-host executor: the node caches the conversation, and only runs the
// agent when it receives a TurnToken. The agent's reply flows downstream as
// chat messages, and a fresh TurnToken travels with it so the next agent
// takes its turn.
//
// This program shows two ways to build a Drafter -> Reviewer pipeline:
//   1. AgentWorkflowBuilder.BuildSequential(...) — the one-liner.
//   2. WorkflowBuilder + BindAsExecutor(agent, options) + AddEdge — the
//      explicit form, so you can see the TurnToken flow and stream the
//      AgentResponseUpdateEvent tokens as they arrive.
//
// Runs against Ollama (llama3.2) by default; see appsettings.json.
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

Console.WriteLine("=== Chapter 12: Agents as Workflow Executors ===\n");

// ---------------------------------------------------------------
// Two agents. Each is an ordinary ChatClientAgent — nothing about
// it is "workflow-aware." The workflow wraps them.
// ---------------------------------------------------------------
AIAgent drafter = new ChatClientAgent(
    chatClient,
    instructions: "You are a concise technical writer. Draft a short paragraph " +
                  "(3-4 sentences) on the topic you are given. Output only the paragraph.",
    name: "Drafter");

AIAgent reviewer = new ChatClientAgent(
    chatClient,
    instructions: "You are a sharp editor. Improve the paragraph you receive for " +
                  "clarity and flow, then return the improved version. Output only the " +
                  "revised paragraph.",
    name: "Reviewer");

string topic = "why immutable data structures reduce bugs in concurrent code";

// ---------------------------------------------------------------
// Approach 1: AgentWorkflowBuilder.BuildSequential — the one-liner.
// The helper wires the agents in order and manages the TurnToken
// handshake for you. The workflow output is the full conversation.
// ---------------------------------------------------------------
Console.WriteLine("--- Approach 1: AgentWorkflowBuilder.BuildSequential ---\n");

Workflow sequential = AgentWorkflowBuilder.BuildSequential(drafter, reviewer);

Run seqRun = await InProcessExecution.RunAsync(
    sequential, new ChatMessage(ChatRole.User, topic),
    sessionId: Guid.NewGuid().ToString(), cts.Token);

foreach (WorkflowEvent evt in seqRun.OutgoingEvents)
{
    if (evt is WorkflowOutputEvent output && output.As<List<ChatMessage>>() is { } conversation)
    {
        foreach (ChatMessage message in conversation.Where(m => m.Role == ChatRole.Assistant))
        {
            Console.WriteLine($"[{message.AuthorName ?? "assistant"}]");
            Console.WriteLine(message.Text);
            Console.WriteLine();
        }
    }
}

// ---------------------------------------------------------------
// Approach 2: explicit WorkflowBuilder, so the TurnToken handshake
// and streaming AgentResponseUpdateEvent tokens are visible.
//
// BindAsExecutor(agent, options) wraps an agent as a host executor.
// EmitAgentUpdateEvents surfaces token-by-token updates; the host
// passes a TurnToken downstream after each agent completes its turn.
// ---------------------------------------------------------------
Console.WriteLine("--- Approach 2: explicit wiring + streamed tokens ---\n");

AIAgentHostOptions hostOptions = new()
{
    EmitAgentResponseEvents = true,
    EmitAgentUpdateEvents = true,
};

ExecutorBinding drafterNode = drafter.BindAsExecutor(hostOptions);
ExecutorBinding reviewerNode = reviewer.BindAsExecutor(hostOptions);

Workflow explicitWorkflow = new WorkflowBuilder(drafterNode)
    .AddEdge(drafterNode, reviewerNode)
    .Build();

StreamingRun stream = await InProcessExecution.RunStreamingAsync(
    explicitWorkflow, new ChatMessage(ChatRole.User, topic),
    sessionId: Guid.NewGuid().ToString(), cts.Token);

// Send a TurnToken so the first agent begins its turn.
await stream.TrySendMessageAsync(new TurnToken(emitEvents: true));

string? currentAuthor = null;
await foreach (WorkflowEvent evt in stream.WatchStreamAsync(cts.Token))
{
    switch (evt)
    {
        case AgentResponseUpdateEvent update:
            // Token-level streaming. Print a header when the speaker changes.
            string author = update.Update.AuthorName ?? update.ExecutorId;
            if (author != currentAuthor)
            {
                currentAuthor = author;
                Console.Write($"\n[{author}] ");
            }
            Console.Write(update.Update.Text);
            break;

        case WorkflowOutputEvent:
            Console.WriteLine("\n");
            break;
    }
}

Console.WriteLine("=== Agent workflow demo complete! ===");
