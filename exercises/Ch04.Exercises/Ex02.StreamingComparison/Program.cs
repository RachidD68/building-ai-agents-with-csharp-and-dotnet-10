// ============================================================================
// Exercise 02 — Streaming Comparison (Intermediate)
// Run the SAME prompt through RunAsync and RunStreamingAsync. For streaming,
// record the Stopwatch timestamp of every update as it arrives, then print a
// console bar chart of the token-arrival timings — one bar per update, bar
// length = elapsed ms (one '#' per 50 ms, exactly the hint's
// new string('#', elapsedMs / 50)). Eyeball the provider's output rate.
// A TTFT/total comparison summary closes the run. FULLY STANDALONE.
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

try
{
    // ---------------------------------------------------------------
    // Non-streaming run (RunAsync) — one timing: the whole response
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
    Console.WriteLine("  Time to first token: same as total — nothing arrives until everything has.");

    // ---------------------------------------------------------------
    // Streaming run (RunStreamingAsync) — record every update's arrival
    // ---------------------------------------------------------------
    Console.WriteLine("\n--- Streaming (RunStreamingAsync) ---\n");

    AgentSession streamSession = await agent.CreateSessionAsync(cts.Token);

    // One entry per update received: (ms since request start, update text).
    List<(long ElapsedMs, string Text)> arrivals = [];

    Stopwatch swStream = Stopwatch.StartNew();

    Console.Write("Response: ");

    await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(
        prompt, streamSession, cancellationToken: cts.Token))
    {
        if (update.Text is { Length: > 0 } text)
        {
            // Stopwatch.Elapsed gives millisecond precision — timestamp the
            // update the moment it arrives.
            arrivals.Add((swStream.ElapsedMilliseconds, text));
            Console.Write(text);
        }
    }

    swStream.Stop();
    long streamTotalMs = swStream.ElapsedMilliseconds;
    long firstTokenMs = arrivals.Count > 0 ? arrivals[0].ElapsedMs : streamTotalMs;

    Console.WriteLine("\n");

    // ---------------------------------------------------------------
    // Token-arrival bar chart — one bar per update, 1 '#' = 50 ms
    // ---------------------------------------------------------------
    Console.WriteLine("--- Token-Arrival Timings ---\n");
    Console.WriteLine($"  {arrivals.Count} updates. Bar length = arrival time since request start (1 '#' = 50 ms).\n");

    foreach ((long elapsedMs, string text) in arrivals)
    {
        // The hint's visualization, verbatim: new string('#', elapsedMs / 50).
        string bar = new string('#', (int)(elapsedMs / 50));

        // Label: elapsed ms + the update's text, truncated to keep rows tidy.
        string token = text.ReplaceLineEndings(" ");
        if (token.Length > 12)
        {
            token = token[..12];
        }

        Console.WriteLine($"  {elapsedMs,6} ms | {bar} \"{token}\"");
    }

    // ---------------------------------------------------------------
    // Comparison summary — TTFT and total time, side by side
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
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled]");
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
    string provider = configuration["LlmProvider:Provider"]?.ToLowerInvariant() ?? "ollama";

    return provider switch
    {
        "azure" => new AzureOpenAIClient(
                new Uri(configuration["LlmProvider:AzureOpenAI:Endpoint"] ?? throw new InvalidOperationException("Missing AzureOpenAI:Endpoint")),
                new DefaultAzureCredential())
            .GetChatClient(configuration["LlmProvider:AzureOpenAI:Model"] ?? "gpt-5.6-luna")
            .AsIChatClient(),

        "openai" => new OpenAI.OpenAIClient(
                configuration["LlmProvider:OpenAI:ApiKey"] ?? throw new InvalidOperationException("Missing OpenAI:ApiKey"))
            .GetChatClient(configuration["LlmProvider:OpenAI:Model"] ?? "gpt-5.6-luna")
            .AsIChatClient(),

        "ollama" => new OllamaSharp.OllamaApiClient(
            new Uri(configuration["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"),
            configuration["LlmProvider:Ollama:Model"] ?? "llama3.2"),

        _ => throw new InvalidOperationException($"Unknown provider: '{provider}'")
    };
}
