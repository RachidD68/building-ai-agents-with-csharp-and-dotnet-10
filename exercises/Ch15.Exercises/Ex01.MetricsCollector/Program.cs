// ============================================================================
// Exercise 01 — Metrics Collector (Beginner)
// Agent that tracks custom metrics: total requests, average response time,
// total tokens used. Display summary after 5 interactions. FULLY STANDALONE.
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

Console.WriteLine("=== Exercise: Metrics Collector ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

ChatClientAgent agent = new(chatClient,
    instructions: "You are a helpful assistant. Be concise (2-3 sentences max).",
    name: "MetricsAgent");

// Metrics tracking
List<RequestMetric> metrics = [];

string[] prompts =
[
    "What is C#?",
    "Explain async/await in one sentence.",
    "What is dependency injection?",
    "Name 3 SOLID principles.",
    "What is the difference between struct and class?"
];

foreach (string prompt in prompts)
{
    Console.WriteLine($"You: {prompt}");

    try
    {
        AgentSession session = await agent.CreateSessionAsync(cts.Token);
        Stopwatch sw = Stopwatch.StartNew();

        AgentResponse response = await agent.RunAsync(
            prompt, session, cancellationToken: cts.Token);

        sw.Stop();

        long inputTokens = response.Usage?.InputTokenCount ?? 0;
        long outputTokens = response.Usage?.OutputTokenCount ?? 0;

        metrics.Add(new RequestMetric(
            prompt, sw.ElapsedMilliseconds, inputTokens, outputTokens));

        Console.WriteLine($"Agent: {response.Text}");
        Console.WriteLine($"  [{sw.ElapsedMilliseconds}ms, {inputTokens}+{outputTokens} tokens]\n");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n[Cancelled]");
        return;
    }
}

// Display metrics summary
Console.WriteLine(new string('=', 60));
Console.WriteLine("=== Metrics Summary ===\n");
Console.WriteLine($"  Total requests     : {metrics.Count}");
Console.WriteLine($"  Total time         : {metrics.Sum(m => m.ResponseTimeMs)}ms");
Console.WriteLine($"  Average time       : {metrics.Average(m => m.ResponseTimeMs):F0}ms");
Console.WriteLine($"  Min time           : {metrics.Min(m => m.ResponseTimeMs)}ms");
Console.WriteLine($"  Max time           : {metrics.Max(m => m.ResponseTimeMs)}ms");
Console.WriteLine($"  Total input tokens : {metrics.Sum(m => m.InputTokens)}");
Console.WriteLine($"  Total output tokens: {metrics.Sum(m => m.OutputTokens)}");
Console.WriteLine($"  Total tokens       : {metrics.Sum(m => m.InputTokens + m.OutputTokens)}");

Console.WriteLine("\n--- Per-Request Breakdown ---\n");
Console.WriteLine($"  {"#",-3} {"Time (ms)",10} {"In Tok",8} {"Out Tok",8} {"Prompt",-35}");
Console.WriteLine($"  {new string('-', 3)} {new string('-', 10)} {new string('-', 8)} {new string('-', 8)} {new string('-', 35)}");

for (int i = 0; i < metrics.Count; i++)
{
    RequestMetric m = metrics[i];
    string truncated = m.Prompt.Length > 35 ? m.Prompt[..32] + "..." : m.Prompt;
    Console.WriteLine($"  {i + 1,-3} {m.ResponseTimeMs,10} {m.InputTokens,8} {m.OutputTokens,8} {truncated,-35}");
}

Console.WriteLine("\n=== Exercise complete! ===");

/// <summary>Creates an IChatClient from configuration.</summary>
static IChatClient CreateChatClient(IConfiguration configuration)
{
    string provider = configuration["LlmProvider:Provider"]?.ToLowerInvariant() ?? "github";
    return provider switch
    {
        "azure" => new AzureOpenAIClient(new Uri(configuration["LlmProvider:AzureOpenAI:Endpoint"] ?? throw new InvalidOperationException("Missing")), new DefaultAzureCredential())
            .GetChatClient(configuration["LlmProvider:AzureOpenAI:Model"] ?? "gpt-4o-mini").AsIChatClient(),
        "openai" => new OpenAI.OpenAIClient(configuration["LlmProvider:OpenAI:ApiKey"] ?? throw new InvalidOperationException("Missing"))
            .GetChatClient(configuration["LlmProvider:OpenAI:Model"] ?? "gpt-4o-mini").AsIChatClient(),
        "github" => new OpenAI.OpenAIClient(new ApiKeyCredential(configuration["LlmProvider:GitHub:Token"] ?? throw new InvalidOperationException("Missing")),
                new OpenAI.OpenAIClientOptions { Endpoint = new Uri(configuration["LlmProvider:GitHub:Endpoint"] ?? "https://models.github.ai/inference") })
            .GetChatClient(configuration["LlmProvider:GitHub:Model"] ?? "openai/gpt-4o-mini").AsIChatClient(),
        "ollama" => new OllamaSharp.OllamaApiClient(new Uri(configuration["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"), configuration["LlmProvider:Ollama:Model"] ?? "llama3.2"),
        _ => throw new InvalidOperationException($"Unknown provider: '{provider}'")
    };
}

/// <summary>Metrics for a single request.</summary>
record RequestMetric(string Prompt, long ResponseTimeMs, long InputTokens, long OutputTokens);
