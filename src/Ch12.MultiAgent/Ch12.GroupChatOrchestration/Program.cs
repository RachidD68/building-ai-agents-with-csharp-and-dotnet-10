// ============================================================================
// Chapter 12 — Group Chat orchestration (MAF 1.14)
// ----------------------------------------------------------------------------
// The built-in Group Chat orchestration runs several agents in a SHARED
// conversation, taking turns under a manager that decides who speaks next and
// when to stop. The framework synchronizes context for you — each agent sees
// the full conversation before its turn — so you do NOT hand-roll a
// List<ChatMessage> the way the chapter's Debate exercise does.
//
// Here a Writer and a Reviewer iterate on a product tagline. The manager is a
// RoundRobinGroupChatManager: it alternates Writer → Reviewer → Writer → ...
// and terminates after a fixed number of turns.
//
// AgentWorkflowBuilder.CreateGroupChatBuilderWith(managerFactory) takes a
// factory that builds the manager from the participant list; AddParticipants
// registers the agents; Build() returns a Workflow. The final shared transcript
// arrives as a WorkflowOutputEvent whose Data is a List<ChatMessage>.
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

Console.WriteLine("=== Chapter 12: Group Chat orchestration ===");
Console.WriteLine();

// ── Two collaborators in one shared conversation ────────────────────
ChatClientAgent writer = new(
    chatClient,
    instructions: """
        You are a copywriter. Propose a single short product tagline (under 10
        words). If a reviewer gives feedback, revise and propose a new one.
        Reply with just the tagline and one sentence of rationale.
        """,
    name: "Writer",
    description: "Proposes and revises product taglines.");

ChatClientAgent reviewer = new(
    chatClient,
    instructions: """
        You are a brand reviewer. Critique the latest tagline in one or two
        sentences: is it clear, distinctive, and on-brand? End with either
        "APPROVED" or a specific change to make.
        """,
    name: "Reviewer",
    description: "Critiques taglines and asks for revisions.");

// ── Build the group chat with a round-robin manager ─────────────────
// The manager alternates speakers and stops after MaximumIterationCount turns.
// Passing a null termination func uses the default iteration-count behavior.
Workflow workflow = AgentWorkflowBuilder
    .CreateGroupChatBuilderWith(agents =>
        new RoundRobinGroupChatManager(agents, shouldTerminateFunc: null)
        {
            MaximumIterationCount = 4 // two rounds: Writer, Reviewer, Writer, Reviewer
        })
    .AddParticipants([writer, reviewer])
    .WithName("TaglineRoom")
    .Build();

// ── Run it ──────────────────────────────────────────────────────────
const string brief =
    "Write a tagline for a developer tool that turns a C# class into a REST API " +
    "with one attribute. Audience: backend .NET developers.";

Console.WriteLine($"Brief: {brief}");
Console.WriteLine();
Console.WriteLine("Group chat (Writer <-> Reviewer):");
Console.WriteLine();

Run run = await InProcessExecution.Default.RunAsync(
    workflow, brief,
    sessionId: Guid.NewGuid().ToString(),
    cts.Token);

foreach (WorkflowEvent evt in run.OutgoingEvents)
{
    if (evt is WorkflowOutputEvent { Data: List<ChatMessage> transcript })
    {
        foreach (ChatMessage message in transcript)
        {
            if (string.IsNullOrWhiteSpace(message.Text))
            {
                continue;
            }

            string who = string.IsNullOrWhiteSpace(message.AuthorName)
                ? message.Role.ToString()
                : message.AuthorName;
            Console.WriteLine($"[{who}] {message.Text}");
            Console.WriteLine();
        }
    }
}

Console.WriteLine("=== Done ===");
