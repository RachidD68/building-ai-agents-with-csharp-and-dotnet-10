// ============================================================================
// Contoso expense help: streaming with an explicit terminal status
// ----------------------------------------------------------------------------
// The same policy agent, streamed. Text is printed as it arrives, and every
// run ends in exactly one state: Completed, Cancelled, Truncated or Failed.
// Text that arrived before a cancellation or a cut-off is kept, but it is
// never reported as a finished answer. Press Ctrl+C during the first answer
// to see a cancelled run.
//
//   dotnet run    against the configured model (Ollama llama3.2 by default)
//
// RunTracker and AgentRuns (AgentRuns.cs) are tested offline in Ch04.AgentTests.
// ============================================================================

using Ch04.FirstAgent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);
using IHost host = builder.Build();
ChatClientAgent agent = ContosoPolicy.Create(host.Services.GetRequiredService<IChatClient>());

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Contoso expense help: streaming ===\n");

RunOutcome first = await Stream("Who reviews a 180 EUR hotel claim, and why?");
if (first.Status == RunStatus.Cancelled) return 130;

// The same agent with a 12-token ceiling for this call: the answer is cut off.
await Stream("Explain the whole expense policy.",
    new ChatClientAgentRunOptions(new ChatOptions { MaxOutputTokens = 12 }));

// Streaming transport, one object at the end: ToAgentResponseAsync merges the updates.
AgentResponse merged = await agent
    .RunStreamingAsync("Is a 42 EUR taxi receipt approved automatically?", cancellationToken: cts.Token)
    .ToAgentResponseAsync(cts.Token);
Console.WriteLine($"Merged: {merged.Text.Trim()}");
Console.WriteLine($"  [finish: {merged.FinishReason?.Value ?? "none"}]");
return 0;

async Task<RunOutcome> Stream(string question, AgentRunOptions? options = null)
{
    Console.WriteLine($"You: {question}");
    Console.Write($"{agent.Name}: ");
    RunOutcome outcome = await AgentRuns.CollectAsync(
        agent.RunStreamingAsync(question, options: options, cancellationToken: cts.Token),
        cts.Token,
        Console.Write);
    Console.WriteLine();
    Console.WriteLine(outcome.Status switch
    {
        RunStatus.Completed => "  [completed]",
        RunStatus.Truncated => $"  [truncated: the answer hit its token limit after {outcome.Text.Length} characters]",
        RunStatus.Cancelled => $"  [cancelled: {outcome.Text.Length} characters arrived, not an answer]",
        _ => $"  [failed: {outcome.Error?.GetType().Name}: {outcome.Error?.Message}]",
    });
    Console.WriteLine();
    return outcome;
}
