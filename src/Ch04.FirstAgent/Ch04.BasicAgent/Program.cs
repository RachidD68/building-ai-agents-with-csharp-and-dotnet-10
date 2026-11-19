// ============================================================================
// Chapter 5 — Hello, Agent! (Basic Agent)
// Your first agent using the Microsoft Agent Framework (MAF).
// We create a ChatClientAgent with a fun pirate persona, send it a message,
// and get a complete (non-streaming) response.
//
// FORWARD REFERENCE: In Chapter 15, we'll see how to add middleware to this
// agent pipeline for logging, timing, and content filtering.
// ============================================================================

using System.ClientModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

// ---------------------------------------------------------------
// Step 1: Build the host with configuration and DI
// ---------------------------------------------------------------
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

Console.WriteLine("=== Chapter 5: Hello, Agent! (Basic Agent) ===\n");

// Wire Ctrl-C to a CancellationTokenSource. Two reasons:
//   1. The app shuts down gracefully instead of being hard-killed.
//   2. The token flows through to the provider, so an abandoned call actually
//      stops generating — which stops the token meter (and the bill) running.
using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ---------------------------------------------------------------
// Step 2: Create a ChatClientAgent with a pirate persona
// ---------------------------------------------------------------
// The simplest way — pass instructions directly to the constructor.
// Instructions define the agent's persona and behaviour rules.
string pirateInstructions = """
    You are Captain CodeBeard, a legendary pirate who sailed the Seven Seas of Software.
    You speak in pirate dialect, love puns about programming, and always end your
    responses with a pirate-themed programming tip.
    Keep your answers concise — three to five sentences at most.
    """;

ChatClientAgent pirateAgent = new(
    chatClient,
    instructions: pirateInstructions,
    name: "CaptainCodeBeard",
    description: "A pirate-themed AI assistant for programming questions");

Console.WriteLine($"Agent created: {pirateAgent.Name}");
Console.WriteLine($"Description  : {pirateAgent.Description}\n");

// ---------------------------------------------------------------
// Step 3: The one-shot call — no session needed
// ---------------------------------------------------------------
// By default the agent is stateless: each run starts with no history. For a
// single, self-contained question you don't need a session at all. Omit it
// and MAF creates a throwaway session internally just for this call.
string userMessage = "What is dependency injection and why should I use it?";
Console.WriteLine($"You: {userMessage}\n");

try
{
    AgentResponse response = await pirateAgent.RunAsync(
        userMessage,
        cancellationToken: cts.Token);

    Console.WriteLine($"{pirateAgent.Name}: {response.Text}\n");

    // Display usage if available
    if (response.Usage is { } usage)
    {
        Console.WriteLine($"[Usage: {usage.InputTokenCount ?? 0} input, " +
                          $"{usage.OutputTokenCount ?? 0} output, " +
                          $"{usage.TotalTokenCount ?? 0} total tokens]");
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled by user]");
    return;
}
// A failed first call is the single most common thing a new reader hits. These
// are the real errors the provider surfaces through IChatClient — not just
// cancellation. We log something useful and stop; Chapter 14 adds real
// resilience (retries, backoff, circuit breakers) around this exact call.
catch (ClientResultException ex)
{
    // OpenAI-compatible providers (OpenAI, Azure OpenAI) throw
    // ClientResultException; ex.Status carries the HTTP status code.
    string reason = ex.Status switch
    {
        429 => "Rate limited (HTTP 429) — you're sending requests too fast, or you've hit a quota. Back off and retry.",
        400 => "Bad request (HTTP 400) — often a content-filter rejection: the provider refused to answer.",
        >= 500 => $"Transient server error (HTTP {ex.Status}) — a provider-side blip. Safe to retry.",
        _ => $"Provider returned HTTP {ex.Status}."
    };
    Console.WriteLine($"[Agent call failed] {reason}");
    Console.WriteLine($"  Detail: {ex.Message}");
    return;
}

// ---------------------------------------------------------------
// Step 4: Per-call option overrides with ChatClientAgentRunOptions
// ---------------------------------------------------------------
// Set defaults at construction; override per request when you need to. Here the
// SAME agent answers the same question twice at two different temperatures —
// once near-deterministic, once loose and creative — without rebuilding it.
Console.WriteLine("\n--- Per-call options: same agent, two temperatures ---\n");

string creativePrompt = "Give me a name for a code review bot.";

try
{
    ChatClientAgentRunOptions cool = new(new ChatOptions { Temperature = 0.0f });
    AgentResponse coolReply = await pirateAgent.RunAsync(
        creativePrompt, options: cool, cancellationToken: cts.Token);
    Console.WriteLine($"Temp 0.0 → {coolReply.Text}\n");

    ChatClientAgentRunOptions warm = new(new ChatOptions { Temperature = 1.0f });
    AgentResponse warmReply = await pirateAgent.RunAsync(
        creativePrompt, options: warm, cancellationToken: cts.Token);
    Console.WriteLine($"Temp 1.0 → {warmReply.Text}");
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled by user]");
    return;
}

// ---------------------------------------------------------------
// Step 5: Alternative creation pattern — ChatClientAgentOptions
// ---------------------------------------------------------------
// For more control, use the options object. This is the pattern to reach for
// when you need to pin chat options, or add history/context providers (Ch 6).
Console.WriteLine("\n--- Alternative: Using ChatClientAgentOptions ---\n");

ChatClientAgentOptions options = new()
{
    Name = "NavigatorNova",
    Description = "A space-explorer AI that explains concepts using space metaphors",
    ChatOptions = new ChatOptions
    {
        Temperature = 0.7f,
        MaxOutputTokens = 300
    }
};

ChatClientAgent spaceAgent = new(
    chatClient,
    options: options);

try
{
    AgentResponse spaceResponse = await spaceAgent.RunAsync(
        "Explain async/await in C# like you're a space explorer.",
        cancellationToken: cts.Token);

    Console.WriteLine($"{spaceAgent.Name}: {spaceResponse.Text}\n");
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled by user]");
    return;
}

// ---------------------------------------------------------------
// Step 6: The AsAIAgent() extension method
// ---------------------------------------------------------------
// If you already have an IChatClient, you can convert it to an agent inline.
// This is handy for quick prototyping.
Console.WriteLine("--- Alternative: IChatClient.AsAIAgent() ---\n");

AIAgent quickAgent = chatClient.AsAIAgent(
    instructions: "You are a helpful assistant. Be concise.",
    name: "QuickHelper");

try
{
    AgentResponse quickResponse = await quickAgent.RunAsync(
        "What is LINQ in one sentence?",
        cancellationToken: cts.Token);

    Console.WriteLine($"{quickAgent.Name}: {quickResponse.Text}");
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled by user]");
}

Console.WriteLine("\n=== Basic Agent demo complete! ===");
