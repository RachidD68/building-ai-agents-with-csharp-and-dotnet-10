// ============================================================================
// Chapter 11 — Handoff orchestration (MAF 1.10)
// ----------------------------------------------------------------------------
// The built-in Handoff orchestration transfers control between agents based on
// context. A triage agent fields the first message, then hands off to the
// right specialist — exactly the customer-support routing the chapter's
// hand-rolled "support-router challenge" builds by hand with handoff_to_* tools.
//
// Under the hood, each handoff is a tool call the framework wires for you, so
// the agents must support tool calling (llama3.2 does). You declare the
// handoff edges; the model decides when to take them.
//
//   SupportTriage ──► BillingSpecialist
//                 ──► TechnicalSpecialist
//                 ──► Supervisor   (escalation)
//
// AgentWorkflowBuilder.CreateHandoffBuilderWith(triage) starts the graph at the
// triage agent; WithHandoffs(from, [targets]) adds the edges; Build() returns a
// Workflow. The final conversation transcript arrives as a WorkflowOutputEvent
// whose Data is a List<ChatMessage>.
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

Console.WriteLine("=== Chapter 11: Handoff orchestration ===");
Console.WriteLine();

// ── The triage agent and three specialists ─────────────────────────
ChatClientAgent triage = new(
    chatClient,
    instructions: """
        You are a customer-support triage agent. Read the customer's message and
        hand off to the right specialist: billing questions to the billing
        specialist, technical problems to the technical specialist, and angry or
        refund-demanding customers to the supervisor. Do not try to solve the
        problem yourself — route it.
        """,
    name: "SupportTriage",
    description: "Routes customer messages to the right support specialist.");

ChatClientAgent billing = new(
    chatClient,
    instructions: """
        You are a billing specialist. Answer billing, invoice, and payment
        questions clearly in 2-3 sentences. If the customer demands a refund,
        say a supervisor must approve it.
        """,
    name: "BillingSpecialist",
    description: "Handles billing, invoice, and payment questions.");

ChatClientAgent technical = new(
    chatClient,
    instructions: """
        You are a technical-support specialist. Help with login failures,
        errors, and product issues. Give one concrete next step in 2-3 sentences.
        """,
    name: "TechnicalSpecialist",
    description: "Handles technical problems and product errors.");

ChatClientAgent supervisor = new(
    chatClient,
    instructions: """
        You are a support supervisor. Handle escalations and refund approvals
        with empathy. Acknowledge the frustration, state a decision, and give a
        clear next step in 2-3 sentences.
        """,
    name: "Supervisor",
    description: "Handles escalations and refund approvals.");

// ── Wire the handoff graph: triage routes to each specialist ────────
Workflow workflow = AgentWorkflowBuilder
    .CreateHandoffBuilderWith(triage)
    .WithHandoffs(triage, [billing, technical, supervisor])
    .WithName("SupportRouter")
    .Build();

// ── Run one customer message through the router ─────────────────────
const string customerMessage =
    "I was charged twice for my subscription this month and I want one of those " +
    "charges refunded. This is the second time it has happened and I am not happy.";

Console.WriteLine($"Customer: {customerMessage}");
Console.WriteLine();
Console.WriteLine("Routing...");
Console.WriteLine();

Run run = await InProcessExecution.Default.RunAsync(
    workflow, customerMessage,
    sessionId: Guid.NewGuid().ToString(),
    cts.Token);

// The transcript (every turn, including the handoff) is the final output.
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
