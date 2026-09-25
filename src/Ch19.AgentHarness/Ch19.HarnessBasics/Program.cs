// ============================================================================
// Chapter 19 — The Agent Harness: your first harness agent (MAF 1.22)
// ----------------------------------------------------------------------------
// Everything Part V built by hand -- a chat client wrapped in middleware, a
// context provider for memory, tool approval, OpenTelemetry -- the harness
// composes for you behind one call:
//
//     HarnessAgent agent = chatClient.AsHarnessAgent(options);
//
// The result is an ordinary AIAgent (HarnessAgent derives from
// DelegatingAIAgent), so sessions, streaming and context providers work
// exactly as they did in Chapters 5 and 6. What changes is what you get for
// free: a to-do list the model maintains, plan/execute agent modes, file
// memory, skills discovery, tool-approval policy and OpenTelemetry -- all on
// by default, each one switched off by an explicit flag.
//
// This first project shows the smallest possible harness agent and prints
// what the harness added, so the defaults are visible rather than magic.
//
// NOTE ON PROVIDERS: the harness adds a HostedWebSearchTool by default, which
// only cloud providers implement. Against Ollama we set DisableWebSearch so
// the local run does not advertise a tool the backend cannot serve.
// ============================================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

HostApplicationBuilder hostBuilder = Host.CreateApplicationBuilder(args);
// A harness agent sends a much larger tool schema than a plain chat call and
// then loops, so the two-minute per-attempt envelope that suits Chapter 5 is
// too tight on local hardware. Widen it rather than let Polly cancel a call
// that was going to succeed.
hostBuilder.Services.AddLlmChatClient(hostBuilder.Configuration,
    attemptTimeout: TimeSpan.FromMinutes(8));

using IHost host = hostBuilder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

string provider = hostBuilder.Configuration["LlmProvider:Provider"]?.ToLowerInvariant() ?? "ollama";
bool isLocal = provider is "ollama";

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Chapter 19: The Agent Harness (basics) ===");
Console.WriteLine();

// ── The whole composition ─────────────────────────────────────────────
// One call. HarnessAgentOptions is where you tune it; every default is
// deliberate, and every default can be switched off.
HarnessAgentOptions options = new()
{
    Name = "ContosoHarness",
    Description = "A support assistant built on the MAF agent harness.",

    // HarnessInstructions sit BETWEEN the harness's own DefaultInstructions
    // and your ChatOptions.Instructions -- three layers, outermost first.
    HarnessInstructions =
        "You are working inside Contoso's support desk. Prefer short, concrete answers.",

    ChatOptions = new ChatOptions
    {
        Instructions = "Answer as a calm senior engineer. Never invent order numbers.",
    },

    // Hosted web search is a cloud-only tool; Ollama cannot serve it.
    DisableWebSearch = isLocal,

    // File memory writes to {cwd}/agent-file-memory/... by default. The
    // basics demo stays in memory so it leaves nothing behind on disk.
    DisableFileMemory = true,
};

HarnessAgent agent = chatClient.AsHarnessAgent(options);

Console.WriteLine($"Agent      : {agent.Name}");
Console.WriteLine($"Type       : {agent.GetType().Name} (derives from {agent.GetType().BaseType?.Name})");
Console.WriteLine();
Console.WriteLine("What the harness switched on for this run:");
Console.WriteLine($"  to-do tracking      : {(options.DisableTodoProvider ? "off" : "ON  (TodoProvider)")}");
Console.WriteLine($"  agent modes         : {(options.DisableAgentModeProvider ? "off" : "ON  (AgentModeProvider: plan / execute)")}");
Console.WriteLine($"  agent skills        : {(options.DisableAgentSkillsProvider ? "off" : "ON  (discovered from the working directory)")}");
Console.WriteLine($"  tool auto-approval  : {(options.DisableToolAutoApproval ? "off" : "ON  (ToolApprovalAgent wraps the agent)")}");
Console.WriteLine($"  OpenTelemetry       : {(options.DisableOpenTelemetry ? "off" : $"ON  (source: {options.OpenTelemetrySourceName ?? "Experimental.Microsoft.Agents.AI"})")}");
Console.WriteLine($"  file memory         : {(options.DisableFileMemory ? "off (this demo)" : "ON")}");
Console.WriteLine($"  hosted web search   : {(options.DisableWebSearch ? "off (local provider)" : "ON")}");
Console.WriteLine();

// ── A session, exactly as in Chapter 6 ────────────────────────────────
// The harness does not invent a new session model. AgentSession works here
// the same way it works for a plain ChatClientAgent.
AgentSession session = await agent.CreateSessionAsync(cts.Token);

string[] turns =
[
    "A customer says their order has not arrived. Outline how you would investigate.",
    "What was the first step you suggested?",
];

foreach (string turn in turns)
{
    Console.WriteLine($"You: {turn}");
    Console.Write("Agent: ");
    await foreach (AgentResponseUpdate update in
        agent.RunStreamingAsync(turn, session, cancellationToken: cts.Token))
    {
        Console.Write(update.Text);
    }
    Console.WriteLine();
    Console.WriteLine();
}

// The second turn answers from history, which is the point: the harness
// carries the conversation without you wiring a chat-history provider.
Console.WriteLine("=== Harness basics demo complete! ===");
