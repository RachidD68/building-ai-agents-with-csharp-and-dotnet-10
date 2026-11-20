// ============================================================================
// Indirect prompt injection: the instruction arrives inside retrieved data
// ----------------------------------------------------------------------------
// The user asks an innocent question. A retrieved document carries the attack.
// Three measures, in the order the prompt is assembled:
//
//   1. Provenance: every chunk carries its source and a trust level
//      (internal handbook vs external upload, email or web page).
//   2. Scan: chunks with instruction-like text are quarantined, not sent.
//   3. Fence: what is sent is wrapped in a boundary with a per-request random
//      id, and any fence-like text inside a chunk is neutralised, so a chunk
//      cannot close its own fence.
//
// The scanner is a signal with misses (the self-test shows one); the fence
// raises the odds, never to certainty. Anything that must not happen is
// refused where it would happen (Ch20.PolicyEnforcement).
//
//   dotnet run                    live run against the configured provider
//   dotnet run -- --self-test     offline: scripted "obedient" model, RESULT n/n
// ============================================================================

using AgenticAI.Common.Extensions;
using Ch20.IndirectInjection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Contains("--self-test"))
    return await IndirectSelfTest.RunAsync();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);
using IHost host = builder.Build();

Console.WriteLine("=== Indirect prompt injection (live; the offline suite runs with --self-test) ===\n");
using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

AIAgent agent = SupportAgent.Create(host.Services.GetRequiredService<IChatClient>());
foreach ((string label, RetrievedChunk[] chunks) in Samples.All)
{
    Assembled prompt = PromptAssembler.Build(Samples.Question, chunks);
    Console.WriteLine($"-- {label}: {prompt.Sent.Count} sent, {prompt.Quarantined.Count} quarantined, external={prompt.IngestedExternal}");
    AgentResponse response = await agent.RunAsync(prompt.Text, cancellationToken: cts.Token);
    Console.WriteLine($"   answer: {response.Text}\n");
}
return 0;
