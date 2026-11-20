// ============================================================================
// The Agent Harness: your first harness agent (MAF 1.28)
// ----------------------------------------------------------------------------
// One call composes a chat client, function calling, tool approval, a to-do
// list, plan/execute modes, file memory, skills discovery, hosted web search
// and OpenTelemetry:
//
//     HarnessAgent agent = chatClient.AsHarnessAgent(options);
//
// HarnessAgent derives from DelegatingAIAgent, so sessions and streaming work
// as they do for any AIAgent. Every default has a Disable* switch, and this
// project prints a capability report after the run: what a bare
// HarnessAgentOptions switches on, what these options leave on, how many tools
// of each kind the model was sent, and how many it called.
//
// Instructions: HarnessInstructions REPLACES HarnessAgent.DefaultInstructions
// when you set it (null keeps the default); ChatOptions.Instructions follows.
// The providers (to-do, modes, skills, memory) add their own text on top.
//
//   dotnet run                  one live run against the configured provider
//   dotnet run -- --self-test   offline checks with a scripted client
// ============================================================================

using AgenticAI.Common.Extensions;
using AgenticAI.Testing;
using Ch16.Harness;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Contains("--self-test")) return await SelfTest.RunAsync();

HostApplicationBuilder hostBuilder = Host.CreateApplicationBuilder(args);
// A harness request carries a large tool surface and then loops, so widen the
// per-attempt timeout rather than let the retry policy cancel a slow local call.
hostBuilder.Services.AddLlmChatClient(hostBuilder.Configuration,
    attemptTimeout: TimeSpan.FromMinutes(8));

using IHost host = hostBuilder.Build();
// The recorder changes nothing; it keeps what each call sent for the report.
RecordingChatClient chatClient = new(host.Services.GetRequiredService<IChatClient>());

string provider = hostBuilder.Configuration["LlmProvider:Provider"]?.ToLowerInvariant() ?? "ollama";
bool isLocal = provider is "ollama";

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== The Agent Harness (basics) ===");
Console.WriteLine();

HarnessAgentOptions options = new()
{
    Name = "ContosoHarness",
    Description = "A support assistant built on the MAF agent harness.",
    HarnessInstructions =
        "You are working inside Contoso's support desk. Prefer short, concrete answers.",
    ChatOptions = new ChatOptions
    {
        Instructions = "Answer as a calm senior engineer. Never invent order numbers.",
    },
    // Hosted web search is on by default; a local Ollama model cannot serve it.
    DisableWebSearch = isLocal,
    // File memory writes under {cwd}/agent-file-memory by default.
    DisableFileMemory = true,
};

HarnessAgent agent = chatClient.AsHarnessAgent(options);
Console.WriteLine($"Agent : {agent.Name} ({agent.GetType().Name} : {agent.GetType().BaseType?.Name})");
Console.WriteLine();

AgentSession session = await agent.CreateSessionAsync(cts.Token);
List<ChatMessage> transcript = [];

string[] turns =
[
    "A customer says their order has not arrived. Outline how you would investigate.",
    "What was the first step you suggested?",
];

foreach (string turn in turns)
{
    Console.WriteLine($"You: {turn}");
    Console.Write("Agent: ");
    List<AgentResponseUpdate> updates = [];
    await foreach (AgentResponseUpdate update in
        agent.RunStreamingAsync(turn, session, cancellationToken: cts.Token))
    {
        updates.Add(update);
        Console.Write(update.Text);
    }
    transcript.AddRange(updates.ToAgentResponse().Messages);
    Console.WriteLine();
    Console.WriteLine();
}

Console.WriteLine("Capability report for this run:");
CapabilityReport.Print(CapabilityReport.Build(options,
    CapabilityReport.Advertised(chatClient.Calls.Select(c => c.Options)),
    CapabilityReport.Called(transcript)), Console.Out);
(int instructionChars, int tools, int schemaChars) = CapabilityReport.Size(chatClient.Calls.FirstOrDefault()?.Options);
Console.WriteLine($"  first request: {instructionChars} instruction chars, {tools} tools, {schemaChars} schema chars");
Console.WriteLine();
Console.WriteLine("=== Harness basics demo complete! ===");
return 0;
