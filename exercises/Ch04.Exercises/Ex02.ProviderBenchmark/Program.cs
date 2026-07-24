// ============================================================================
// Exercise 02 — Provider Benchmark (Intermediate)
// Sends the same prompt to multiple configured providers and compares
// response time and output length. Displays results in a table.
// FULLY STANDALONE — no project references to Common.
// ============================================================================

using System.ClientModel;
using System.Diagnostics;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

using IHost host = builder.Build();
IConfiguration config = host.Services.GetRequiredService<IConfiguration>();

Console.WriteLine("=== Exercise: Provider Benchmark ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

string benchmarkPrompt = "Explain the difference between async and parallel programming in C#. " +
                         "Keep your answer under 100 words.";

Console.WriteLine($"Benchmark prompt: \"{benchmarkPrompt}\"\n");

// ---------------------------------------------------------------
// Try each provider and collect results
// ---------------------------------------------------------------
string[] providerNames = ["github", "ollama"];
List<BenchmarkResult> results = [];

foreach (string providerName in providerNames)
{
    Console.Write($"  Testing {providerName,-10}... ");

    try
    {
        IChatClient chatClient = CreateChatClient(providerName, config);
        Stopwatch sw = Stopwatch.StartNew();

        ChatResponse response = await chatClient.GetResponseAsync(
            benchmarkPrompt,
            cancellationToken: cts.Token);

        sw.Stop();

        string text = response.Text ?? string.Empty;
        long inputTokens = response.Usage?.InputTokenCount ?? 0;
        long outputTokens = response.Usage?.OutputTokenCount ?? 0;

        results.Add(new BenchmarkResult(
            providerName,
            sw.Elapsed,
            text.Length,
            inputTokens,
            outputTokens,
            text));

        Console.WriteLine($"done ({sw.ElapsedMilliseconds}ms)");
    }
    catch (OperationCanceledException) when (cts.IsCancellationRequested)
    {
        // Ctrl+C is the user aborting the benchmark, not a provider failure —
        // recording it as FAILED would poison the comparison table.
        Console.WriteLine("cancelled — stopping the benchmark.");
        break;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAILED: {ex.Message}");
        results.Add(new BenchmarkResult(
            providerName,
            TimeSpan.Zero,
            0, 0, 0,
            $"Error: {ex.Message}"));
    }
}

// ---------------------------------------------------------------
// Display comparison table
// ---------------------------------------------------------------
Console.WriteLine("\n--- Benchmark Results ---\n");
Console.WriteLine($"  {"Provider",-12} {"Time (ms)",10} {"Chars",8} {"In Tok",8} {"Out Tok",8}");
Console.WriteLine($"  {new string('-', 12)} {new string('-', 10)} {new string('-', 8)} {new string('-', 8)} {new string('-', 8)}");

foreach (BenchmarkResult result in results)
{
    Console.WriteLine($"  {result.Provider,-12} {result.Elapsed.TotalMilliseconds,10:F0} " +
                      $"{result.CharCount,8} {result.InputTokens,8} {result.OutputTokens,8}");
}

// ---------------------------------------------------------------
// Show each response
// ---------------------------------------------------------------
Console.WriteLine("\n--- Responses ---\n");

foreach (BenchmarkResult result in results)
{
    Console.WriteLine($"  [{result.Provider}] ({result.CharCount} chars):");
    Console.WriteLine($"  {result.ResponseText}\n");
}

// Rank by speed
BenchmarkResult[] ranked = [.. results
    .Where(r => r.Elapsed > TimeSpan.Zero)
    .OrderBy(r => r.Elapsed)];

if (ranked.Length > 0)
{
    Console.WriteLine($"Fastest provider: {ranked[0].Provider} ({ranked[0].Elapsed.TotalMilliseconds:F0}ms)");
}

Console.WriteLine("\n=== Exercise complete! ===");

// ---------------------------------------------------------------
// Provider factory — fully standalone
// ---------------------------------------------------------------

/// <summary>
/// Creates an <see cref="IChatClient"/> for the specified provider.
/// </summary>
static IChatClient CreateChatClient(string providerName, IConfiguration configuration) =>
    providerName switch
    {
        "azure" => new AzureOpenAIClient(
                new Uri(configuration["LlmProvider:AzureOpenAI:Endpoint"]
                    ?? throw new InvalidOperationException("Missing LlmProvider:AzureOpenAI:Endpoint")),
                new DefaultAzureCredential())
            .GetChatClient(configuration["LlmProvider:AzureOpenAI:Model"] ?? "gpt-4o-mini")
            .AsIChatClient(),

        "openai" => new OpenAI.OpenAIClient(
                configuration["LlmProvider:OpenAI:ApiKey"]
                    ?? throw new InvalidOperationException("Missing LlmProvider:OpenAI:ApiKey"))
            .GetChatClient(configuration["LlmProvider:OpenAI:Model"] ?? "gpt-4o-mini")
            .AsIChatClient(),

        "github" => new OpenAI.OpenAIClient(
                new ApiKeyCredential(
                    configuration["LlmProvider:GitHub:Token"]
                        ?? throw new InvalidOperationException("Missing LlmProvider:GitHub:Token")),
                new OpenAI.OpenAIClientOptions
                {
                    Endpoint = new Uri(
                        configuration["LlmProvider:GitHub:Endpoint"]
                            ?? "https://models.github.ai/inference")
                })
            .GetChatClient(configuration["LlmProvider:GitHub:Model"] ?? "openai/gpt-4o-mini")
            .AsIChatClient(),

        "ollama" => new OllamaSharp.OllamaApiClient(
                new Uri(configuration["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"),
                configuration["LlmProvider:Ollama:Model"] ?? "llama3.2"),

        _ => throw new InvalidOperationException(
            $"Unknown provider: '{providerName}'. Valid: azure, openai, github, ollama.")
    };

// ---------------------------------------------------------------
// Types
// ---------------------------------------------------------------

/// <summary>
/// Holds the benchmark result for a single provider.
/// </summary>
/// <param name="Provider">The provider name.</param>
/// <param name="Elapsed">The total elapsed time.</param>
/// <param name="CharCount">The character count of the response.</param>
/// <param name="InputTokens">The number of input tokens reported.</param>
/// <param name="OutputTokens">The number of output tokens reported.</param>
/// <param name="ResponseText">The full response text.</param>
record BenchmarkResult(
    string Provider,
    TimeSpan Elapsed,
    int CharCount,
    long InputTokens,
    long OutputTokens,
    string ResponseText);
