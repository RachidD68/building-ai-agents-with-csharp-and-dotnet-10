// ============================================================================
// Chapter 11 — Magentic: the Microsoft Research orchestrator (MAF 1.10)
// ----------------------------------------------------------------------------
// Magentic is a multi-agent orchestrator from Microsoft Research that uses a
// stateful "ledger" — a structured record of facts, plans, and observations —
// to coordinate a team of specialist agents toward a goal. MAF drives the
// Magentic-protocol messages for you, so you declare a Magentic team and let
// the framework run the ledger rather than hand-rolling message-passing.
//
// In this demo, three specialists collaborate on a short technical brief:
//   - Researcher: gathers and lists relevant facts about a topic
//   - Writer:     drafts the brief from the researcher's facts
//   - Reviewer:   tightens the draft, fixes weak claims, ships final
//
// MagenticWorkflowBuilder ships in Microsoft.Agents.AI.Workflows. As of the
// 1.9 release the agent orchestrations are stable (no longer [Experimental]),
// so there is no diagnostic to suppress. You give the builder a manager
// (orchestrator) agent, add the specialist participants, and Build() produces a
// Workflow you run like any other. The manager plans, delegates, inspects each
// result against the ledger, and decides when the goal is met — you do not wire
// the edges by hand.
// ============================================================================

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.InProc;
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

Console.WriteLine("=== Chapter 11: Magentic orchestrator ===");
Console.WriteLine();

// ── The three specialists ──────────────────────────────────────────
ChatClientAgent researcher = new(
    chatClient,
    instructions: """
        You are a careful research analyst. Given a topic, list 5 to 8 concrete,
        verifiable facts relevant to a brief about it. One fact per line, no
        prose, no opinions. Each fact stands alone.
        """,
    name: "Researcher",
    description: "Gathers concrete, verifiable facts about the topic.");

ChatClientAgent writer = new(
    chatClient,
    instructions: """
        You are a technical writer. Given a topic and a list of facts, draft a
        brief 3-paragraph technical brief. Use the facts; do not invent new ones.
        Tone: direct, no hedging, no marketing language.
        """,
    name: "Writer",
    description: "Drafts a technical brief from the researcher's facts.");

ChatClientAgent reviewer = new(
    chatClient,
    instructions: """
        You are a senior technical editor. Review the draft. Remove weak claims,
        tighten verbose passages, and verify each statement against the supplied
        facts. Return the final brief and a single-sentence summary of changes.
        """,
    name: "Reviewer",
    description: "Reviews and tightens the writer's draft.");

// ── The manager (orchestrator) that drives the Magentic ledger ─────
ChatClientAgent manager = new(
    chatClient,
    instructions: """
        You coordinate a research team (a Researcher, a Writer, and a
        Reviewer) to produce one short technical brief on the given topic.
        Plan the work, delegate to the right specialist, inspect each
        result, and decide when the brief is good enough to return.
        """,
    name: "Manager",
    description: "Plans, delegates, and decides when the brief is done.");

// ── Wire the team with the real MagenticWorkflowBuilder ────────────
// The manager runs the ledger (facts, plan, observations) and decides who
// works next; MagenticWorkflowBuilder.Build() produces a Workflow we run
// through the same in-process execution environment as any other workflow.
// The orchestration is stable as of MAF 1.9 — no diagnostic to suppress.
// RequirePlanSignoff is TRUE by default: the manager would pause and emit a
// plan-review request for a human to approve before any work starts. This demo
// runs unattended, so we turn signoff off and let the manager proceed on its
// own. (Chapter 12 covers the human-in-the-loop request/response pattern that
// the default signoff uses.)
// NOTE ON MODELS: Magentic's manager runs a planning protocol (structured
// JSON: facts, plan, progress ledger) before and between delegations. A small
// local model such as llama3.2 spends most of its round budget on that protocol
// and may hit WithMaxRounds before shipping a finished brief — you'll see
// "stopped due to hitting the maximum round count limit". A more capable model
// (e.g. gpt-4o-mini via the GitHub or OpenAI provider) completes the brief in
// far fewer rounds. The orchestration API is identical across providers.
Workflow magenticWorkflow = new MagenticWorkflowBuilder(manager)
    .AddParticipants([researcher, writer, reviewer])
    .WithMaxRounds(6)
    .RequirePlanSignoff(false)
    .WithName("BriefTeam")
    .Build();

// ── Run the team ───────────────────────────────────────────────────
string topic = "How agentic AI is changing the role of the .NET developer.";

Console.WriteLine($"Topic: {topic}");
Console.WriteLine();

IWorkflowExecutionEnvironment env = InProcessExecution.Default;
Run run = await env.RunAsync<string>(
    magenticWorkflow, topic,
    sessionId: Guid.NewGuid().ToString(),
    cts.Token);

// The final brief arrives as the WorkflowOutputEvent whose Data is the
// List<ChatMessage> the team produced. The last assistant message is the
// reviewed, shipped brief.
foreach (WorkflowEvent evt in run.OutgoingEvents)
{
    if (evt is WorkflowOutputEvent { Data: List<ChatMessage> messages })
    {
        ChatMessage? final = null;
        foreach (ChatMessage message in messages)
        {
            if (message.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(message.Text))
            {
                final = message;
            }
        }

        if (final is not null)
        {
            Console.WriteLine("=== Final brief ===");
            Console.WriteLine(final.Text);
            Console.WriteLine();
        }
    }
}

Console.WriteLine("=== Done ===");
