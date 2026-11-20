// ============================================================================
// Composing the harness: instructions, switches, your own tools
// ----------------------------------------------------------------------------
// The harness is opinionated, not closed. You shape it three ways:
//
//   1. Instructions. HarnessInstructions replaces HarnessAgent.DefaultInstructions
//      (a public const you can read); null keeps the default and "" sends no
//      harness text at all. ChatOptions.Instructions follows it.
//   2. Opt-out switches. Each Disable* flag removes a capability AND the tools
//      it injected, which is how you keep the request small on a local model.
//   3. Your own tools. ChatOptions.Tools compose with the harness's tools.
//
// The demo builds a full and a minimal agent from the same chat client and
// prints the instruction and schema size each one sends, then asks both the
// same question.
//
//   dotnet run                  live run against the configured provider
//   dotnet run -- --self-test   offline checks with a scripted client
// ============================================================================

using AgenticAI.Common.Extensions;
using AgenticAI.Testing;
using Ch16.Harness;
using Ch16.HarnessComposition;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Contains("--self-test")) return await SelfTest.RunAsync();

HostApplicationBuilder hostBuilder = Host.CreateApplicationBuilder(args);
hostBuilder.Services.AddLlmChatClient(hostBuilder.Configuration,
    attemptTimeout: TimeSpan.FromMinutes(8));

using IHost host = hostBuilder.Build();
IChatClient inner = host.Services.GetRequiredService<IChatClient>();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Composing the harness ===");
Console.WriteLine();
Console.WriteLine($"HarnessAgent.DefaultInstructions: {HarnessAgent.DefaultInstructions.Length} characters, first line:");
Console.WriteLine($"  \"{HarnessAgent.DefaultInstructions.Split('\n')[0].Trim()}\"");
Console.WriteLine();

const string question = "Is order ORD-3003 going to arrive this week?";

foreach ((string label, HarnessAgentOptions options) in
         new[] { ("FullHarness", Composition.Full()), ("MinimalHarness", Composition.Minimal()) })
{
    RecordingChatClient recorder = new(inner);
    HarnessAgent agent = recorder.AsHarnessAgent(options);

    Console.WriteLine($"--- {label} ---");
    Console.WriteLine($"You: {question}");
    Console.Write("Agent: ");
    AgentSession session = await agent.CreateSessionAsync(cts.Token);
    List<AgentResponseUpdate> updates = [];
    await foreach (AgentResponseUpdate update in
        agent.RunStreamingAsync(question, session, cancellationToken: cts.Token))
    {
        updates.Add(update);
        Console.Write(update.Text);
    }
    Console.WriteLine();

    (int instructionChars, int tools, int schemaChars) = CapabilityReport.Size(recorder.Calls.FirstOrDefault()?.Options);
    Console.WriteLine($"[{label}] first request: {instructionChars} instruction chars, {tools} tools, " +
                      $"{schemaChars} schema chars; {recorder.Calls.Count} model calls; " +
                      $"tools called: {string.Join(", ", CapabilityReport.Called(updates.ToAgentResponse().Messages).DefaultIfEmpty("none"))}");
    Console.WriteLine();
}

Console.WriteLine("=== Harness composition demo complete! ===");
return 0;
