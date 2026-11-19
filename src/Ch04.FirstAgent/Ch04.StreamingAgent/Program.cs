// ============================================================================
// Chapter 5 — Hello, Agent! (Streaming Agent)
// The same pirate agent, but now using streaming to see tokens arrive in
// real-time. Streaming is essential for chat UIs where users expect to see
// the response being "typed out" rather than waiting for the full response.
//
// Key difference:
//   RunAsync()          → waits for complete response, returns AgentResponse
//   RunStreamingAsync() → yields AgentResponseUpdate items as they arrive
// ============================================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

// ---------------------------------------------------------------
// Step 1: Build the host (same pattern as BasicAgent)
// ---------------------------------------------------------------
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

Console.WriteLine("=== Chapter 5: Hello, Agent! (Streaming) ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ---------------------------------------------------------------
// Step 2: Create the agent with a pirate persona
// ---------------------------------------------------------------
ChatClientAgent pirateAgent = new(
    chatClient,
    instructions: """
        You are Captain CodeBeard, a legendary pirate who sailed the Seven Seas of Software.
        You speak in pirate dialect, love puns about programming, and always end your
        responses with a pirate-themed programming tip.
        """,
    name: "CaptainCodeBeard",
    description: "A pirate-themed AI assistant for programming questions");

AgentSession session = await pirateAgent.CreateSessionAsync(cts.Token);

// ---------------------------------------------------------------
// Step 3: Streaming response — tokens arrive one at a time
// ---------------------------------------------------------------
string userMessage = "Explain the SOLID principles in software engineering.";
Console.WriteLine($"You: {userMessage}\n");
Console.Write($"{pirateAgent.Name}: ");

try
{
    // RunStreamingAsync returns an IAsyncEnumerable<AgentResponseUpdate>.
    // Each update contains a small chunk of the response (often a single token).
    await foreach (AgentResponseUpdate update in pirateAgent.RunStreamingAsync(
        userMessage,
        session,
        cancellationToken: cts.Token))
    {
        // Write each token immediately — the user sees the response "typing out"
        if (update.Text is { Length: > 0 } text)
        {
            Console.Write(text);
        }
    }

    Console.WriteLine("\n");
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n\n[Cancelled by user]");
    return;
}

// ---------------------------------------------------------------
// Step 4: Side-by-side comparison — non-streaming vs. streaming
// ---------------------------------------------------------------
Console.WriteLine("--- Timing Comparison: Non-Streaming vs. Streaming ---\n");

string comparisonPrompt = "Write a short poem about C# in four lines.";

// Non-streaming: measure time to first output
var nonStreamStart = DateTime.UtcNow;
AgentSession nonStreamSession = await pirateAgent.CreateSessionAsync(cts.Token);

try
{
    AgentResponse fullResponse = await pirateAgent.RunAsync(
        comparisonPrompt,
        nonStreamSession,
        cancellationToken: cts.Token);

    var nonStreamElapsed = DateTime.UtcNow - nonStreamStart;
    Console.WriteLine($"Non-streaming (time to complete response): {nonStreamElapsed.TotalMilliseconds:F0}ms");
    Console.WriteLine($"Response: {fullResponse.Text}\n");
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled by user]");
    return;
}

// Streaming: measure time to first token
var streamStart = DateTime.UtcNow;
TimeSpan? timeToFirstToken = null;
AgentSession streamSession = await pirateAgent.CreateSessionAsync(cts.Token);

try
{
    Console.Write("Streaming: ");
    await foreach (AgentResponseUpdate update in pirateAgent.RunStreamingAsync(
        comparisonPrompt,
        streamSession,
        cancellationToken: cts.Token))
    {
        if (update.Text is { Length: > 0 } text)
        {
            timeToFirstToken ??= DateTime.UtcNow - streamStart;
            Console.Write(text);
        }
    }

    var streamElapsed = DateTime.UtcNow - streamStart;
    Console.WriteLine($"\n\nStreaming time to first token: {timeToFirstToken?.TotalMilliseconds:F0}ms");
    Console.WriteLine($"Streaming total time: {streamElapsed.TotalMilliseconds:F0}ms");
    Console.WriteLine("\nNotice: streaming gives the user something to read almost immediately,");
    Console.WriteLine("even though the total time may be similar.");
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n\n[Cancelled by user]");
}

Console.WriteLine("\n=== Streaming Agent demo complete! ===");
