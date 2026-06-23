// ============================================================================
// Exercise 02 — Streaming Comparison (Intermediate)
// Send the same prompt both streaming and non-streaming.
// Measure and display time-to-first-token vs. total time.
// FULLY STANDALONE.
// ============================================================================

using System.ClientModel;
using System.Diagnostics;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

using IHost host = builder.Build();
IConfiguration config = host.Services.GetRequiredService<IConfiguration>();
IChatClient chatClient = CreateChatClient(config);

Console.WriteLine("=== Exercise: Streaming vs Non-Streaming Comparison ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

string prompt = "Explain the SOLID principles in software engineering. Give a brief description of each.";

ChatClientAgent agent = new(
    chatClient,
    instructions: "You are a helpful programming tutor. Be concise.",
    name: "TutorAgent");

// ---------------------------------------------------------------
// Non-Streaming run
// ---------------------------------------------------------------
Console.WriteLine("--- Non-Streaming (RunAsync) ---\n");

AgentSession nonStreamSession = await agent.CreateSessionAsync(cts.Token);
Stopwatch swNonStream = Stopwatch.StartNew();

AgentResponse nonStreamResponse = await agent.RunAsync(
    prompt, nonStreamSession, cancellationToken: cts.Token);

swNonStream.Stop();
long nonStreamMs = swNonStream.ElapsedMilliseconds;

Console.WriteLine($"Response ({nonStreamResponse.Text?.Length ?? 0} chars):");
Console.WriteLine(nonStreamResponse.Text);
Console.WriteLine($"\n  Total time: {nonStreamMs}ms");
Console.WriteLine($"  Time to first token: {nonStreamMs}ms (same as total for non-streaming)");

// ---------------------------------------------------------------
// Streaming run
// ---------------------------------------------------------------
Console.WriteLine("\n--- Streaming (RunStreamingAsync) ---\n");

AgentSession streamSession = await agent.CreateSessionAsync(cts.Token);
Stopwatch swStream = Stopwatch.StartNew();
long firstTokenMs = 0;
bool isFirst = true;
int totalChars = 0;
int updateCount = 0;

Console.Write("Response: ");

// To get the fully assembled AgentResponse instead of manual concatenation, call updates.ToAgentResponseAsync() (introduced in Chapter 7).
await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(
    prompt, streamSession, cancellationToken: cts.Token))
{
    if (isFirst && !string.IsNullOrEmpty(update.Text))
    {
        firstTokenMs = swStream.ElapsedMilliseconds;
        isFirst = true;
    }

    if (!string.IsNullOrEmpty(update.Text))
    {
        if (isFirst)
        {
            firstTokenMs = swStream.ElapsedMilliseconds;
            isFirst = false;
        }
        Console.Write(update.Text);
        totalChars += update.Text.Length;
        updateCount++;
    }
}

swStream.Stop();
long streamTotalMs = swStream.ElapsedMilliseconds;

Console.WriteLine($"\n\n  Total chars: {totalChars}");
Console.WriteLine($"  Streaming updates: {updateCount}");
Console.WriteLine($"  Time to first token: {firstTokenMs}ms");
Console.WriteLine($"  Total time: {streamTotalMs}ms");

// ---------------------------------------------------------------
// Comparison summary
// ---------------------------------------------------------------
Console.WriteLine("\n--- Comparison Summary ---\n");
Console.WriteLine($"  {"Metric",-25} {"Non-Streaming",15} {"Streaming",15}");
Console.WriteLine($"  {new string('-', 25)} {new string('-', 15)} {new string('-', 15)}");
Console.WriteLine($"  {"Time to first token",-25} {nonStreamMs + "ms",15} {firstTokenMs + "ms",15}");
Console.WriteLine($"  {"Total time",-25} {nonStreamMs + "ms",15} {streamTotalMs + "ms",15}");

if (firstTokenMs > 0 && nonStreamMs > 0)
{
    double speedup = (double)nonStreamMs / firstTokenMs;
    Console.WriteLine($"\n  Streaming first-token speedup: {speedup:F1}x faster");
}

Console.WriteLine("\n=== Exercise complete! ===");

// ---------------------------------------------------------------
// Provider factory
// ---------------------------------------------------------------

/// <summary>
/// Creates an <see cref="IChatClient"/> from User Secrets configuration.
/// </summary>
static IChatClient CreateChatClient(IConfiguration configuration)
{
    string provider = configuration["LlmProvider:Provider"]?.ToLowerInvariant() ?? "github";

    return provider switch
    {
        "azure" => new AzureOpenAIClient(
                new Uri(configuration["LlmProvider:AzureOpenAI:Endpoint"] ?? throw new InvalidOperationException("Missing AzureOpenAI:Endpoint")),
                new DefaultAzureCredential())
            .GetChatClient(configuration["LlmProvider:AzureOpenAI:Model"] ?? "gpt-4o-mini")
            .AsIChatClient(),

        "openai" => new OpenAI.OpenAIClient(
                configuration["LlmProvider:OpenAI:ApiKey"] ?? throw new InvalidOperationException("Missing OpenAI:ApiKey"))
            .GetChatClient(configuration["LlmProvider:OpenAI:Model"] ?? "gpt-4o-mini")
            .AsIChatClient(),

        "github" => new OpenAI.OpenAIClient(
                new ApiKeyCredential(configuration["LlmProvider:GitHub:Token"] ?? throw new InvalidOperationException("Missing GitHub:Token")),
                new OpenAI.OpenAIClientOptions { Endpoint = new Uri(configuration["LlmProvider:GitHub:Endpoint"] ?? "https://models.github.ai/inference") })
            .GetChatClient(configuration["LlmProvider:GitHub:Model"] ?? "openai/gpt-4o-mini")
            .AsIChatClient(),

        "ollama" => new OllamaSharp.OllamaApiClient(
            new Uri(configuration["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"),
            configuration["LlmProvider:Ollama:Model"] ?? "llama3.2"),

        _ => throw new InvalidOperationException($"Unknown provider: '{provider}'")
    };
}
