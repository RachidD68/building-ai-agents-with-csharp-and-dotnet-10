// ============================================================================
// Exercise 02 — Provider Benchmark (Intermediate)
// Asks the same 10 questions of every CONFIGURED provider — one keyed
// IChatClient per provider — and prints a table of provider -> average
// latency -> average output tokens -> estimated cost (the Chapter 3 formula).
// FULLY STANDALONE — no project references to Common; the LlmSettings record
// below mirrors the companion AgenticAI.Common.Configuration.LlmSettings.
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

// Bind the companion LlmSettings shape from the "LlmProvider" section
// (appsettings.json, overridable via environment variables such as
// LlmProvider__OpenAI__ApiKey or User Secrets).
LlmSettings settings = new();
builder.Configuration.GetSection("LlmProvider").Bind(settings);

// ---------------------------------------------------------------
// Which providers are actually configured?
// ---------------------------------------------------------------
// A provider counts as configured when its section carries the one field it
// cannot work without: an endpoint for Azure/Ollama, a key/token for
// OpenAI. Read from the raw section so record defaults don't count.
IConfigurationSection llmSection = builder.Configuration.GetSection("LlmProvider");

(string Name, string RequiredField)[] candidates =
[
    ("azure",  "AzureOpenAI:Endpoint"),
    ("openai", "OpenAI:ApiKey"),
    ("ollama", "Ollama:Endpoint"),
];

string[] providerNames = [.. candidates
    .Where(c => !string.IsNullOrWhiteSpace(llmSection[c.RequiredField]))
    .Select(c => c.Name)];

if (providerNames.Length == 0)
{
    Console.WriteLine("No providers configured. Set at least one required field in the");
    Console.WriteLine("LlmProvider section — e.g. Ollama:Endpoint, OpenAI:ApiKey, OpenAI:ApiKey,");
    Console.WriteLine("or AzureOpenAI:Endpoint — via appsettings.json, User Secrets, or env vars.");
    return;
}

// ---------------------------------------------------------------
// Keyed DI: one IChatClient per configured provider
// ---------------------------------------------------------------
// The companion Ch04.FirstLlmCall registers ONE client with AddLlmChatClient;
// the benchmark swaps that for a loop that registers each configured provider
// under its own keyed service name.
foreach (string providerName in providerNames)
{
    builder.Services.AddKeyedSingleton<IChatClient>(
        providerName,
        (_, key) => CreateChatClient((string)key!, settings));
}

using IHost host = builder.Build();

Console.WriteLine("=== Exercise: Provider Benchmark ===\n");
Console.WriteLine($"Configured providers: {string.Join(", ", providerNames)}\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// The same 10 questions go to every provider. A terse system prompt keeps
// answers (and the benchmark's wall-clock time) short.
const string TerseInstruction = "You are terse. Answer in one short sentence.";

string[] questions =
[
    "What does the 'async' keyword do in C#?",
    "Name one difference between a class and a struct in .NET.",
    "What is NuGet used for?",
    "What does LINQ stand for?",
    "What is the purpose of IDisposable?",
    "What file extension does a C# project file use?",
    "What is a nullable reference type?",
    "What does 'dotnet run' do?",
    "Name one benefit of records over classes.",
    "What is the .NET garbage collector responsible for?",
];

// ---------------------------------------------------------------
// Run the benchmark: every provider answers every question
// ---------------------------------------------------------------
List<ProviderResult> results = [];

foreach (string providerName in providerNames)
{
    if (cts.IsCancellationRequested) break;

    string model = ModelFor(providerName, settings);
    Console.WriteLine($"--- {providerName} ({model}) ---");

    IChatClient chatClient = host.Services.GetRequiredKeyedService<IChatClient>(providerName);

    List<QuestionRun> runs = [];
    string? failure = null;

    for (int i = 0; i < questions.Length; i++)
    {
        Console.Write($"  Q{i + 1,2}: ");

        try
        {
            List<ChatMessage> messages =
            [
                new(ChatRole.System, TerseInstruction),
                new(ChatRole.User, questions[i]),
            ];

            Stopwatch stopwatch = Stopwatch.StartNew();
            ChatResponse response = await chatClient.GetResponseAsync(
                messages,
                cancellationToken: cts.Token);
            stopwatch.Stop();

            long inputTokens = response.Usage?.InputTokenCount ?? 0;
            long outputTokens = response.Usage?.OutputTokenCount ?? 0;

            runs.Add(new QuestionRun(stopwatch.Elapsed, inputTokens, outputTokens));
            Console.WriteLine($"{stopwatch.ElapsedMilliseconds,6} ms, {outputTokens,4} out tokens");
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
            // Provider down or misconfigured: record it and move to the next one.
            failure = ex.Message;
            Console.WriteLine($"FAILED: {ex.Message}");
            break;
        }
    }

    results.Add(new ProviderResult(providerName, model, runs, failure));
    Console.WriteLine();
}

// ---------------------------------------------------------------
// Comparison table: provider -> avg latency -> avg out tokens -> est. cost
// ---------------------------------------------------------------
Console.WriteLine($"--- Benchmark Results ({questions.Length} questions per provider) ---\n");
Console.WriteLine($"  {"Provider",-10} {"Answered",8} {"Avg ms",10} {"Avg out tok",12} {"Est. cost ($)",14}");
Console.WriteLine($"  {new string('-', 10)} {new string('-', 8)} {new string('-', 10)} {new string('-', 12)} {new string('-', 14)}");

foreach (ProviderResult result in results)
{
    if (result.Runs.Count == 0)
    {
        Console.WriteLine($"  {result.Provider,-10} {0,8} {"—",10} {"—",12} {"—",14}  {result.Failure ?? "cancelled"}");
        continue;
    }

    double avgMs = result.Runs.Average(r => r.Elapsed.TotalMilliseconds);
    double avgOutTokens = result.Runs.Average(r => (double)r.OutputTokens);

    // Chapter 3 cost formula: tokens x rate / 1M, input and output priced
    // separately, summed over every answered question.
    (decimal inputPer1M, decimal outputPer1M) = GetPricing(result.Model);
    decimal estimatedCost = result.Runs.Sum(r =>
        (r.InputTokens * inputPer1M) + (r.OutputTokens * outputPer1M)) / 1_000_000m;

    Console.WriteLine($"  {result.Provider,-10} {result.Runs.Count,8} {avgMs,10:F0} {avgOutTokens,12:F1} {estimatedCost,14:F6}");
}

ProviderResult[] ranked = [.. results
    .Where(r => r.Runs.Count > 0)
    .OrderBy(r => r.Runs.Average(x => x.Elapsed.TotalMilliseconds))];

if (ranked.Length > 0)
{
    double fastestMs = ranked[0].Runs.Average(x => x.Elapsed.TotalMilliseconds);
    Console.WriteLine($"\nFastest provider: {ranked[0].Provider} ({fastestMs:F0} ms average)");
}

Console.WriteLine("\n=== Exercise complete! ===");

// ---------------------------------------------------------------
// Provider factory — fully standalone
// ---------------------------------------------------------------

/// <summary>
/// Creates an <see cref="IChatClient"/> for the specified provider from the
/// bound <see cref="LlmSettings"/>.
/// </summary>
/// <param name="providerName">The provider name: azure, openai, github, or ollama.</param>
/// <param name="settings">The bound LlmProvider settings.</param>
/// <returns>A configured <see cref="IChatClient"/>.</returns>
static IChatClient CreateChatClient(string providerName, LlmSettings settings) =>
    providerName switch
    {
        "azure" => new AzureOpenAIClient(
                new Uri(settings.AzureOpenAI.Endpoint),
                new DefaultAzureCredential())
            .GetChatClient(settings.AzureOpenAI.Model)
            .AsIChatClient(),

        "openai" => new OpenAI.OpenAIClient(
                new ApiKeyCredential(settings.OpenAI.ApiKey))
            .GetChatClient(settings.OpenAI.Model)
            .AsIChatClient(),

        "ollama" => new OllamaSharp.OllamaApiClient(
                new Uri(settings.Ollama.Endpoint),
                settings.Ollama.Model),

        _ => throw new InvalidOperationException(
            $"Unknown provider: '{providerName}'. Valid: azure, openai, github, ollama.")
    };

/// <summary>
/// Returns the configured model id for a provider.
/// </summary>
static string ModelFor(string providerName, LlmSettings settings) => providerName switch
{
    "azure" => settings.AzureOpenAI.Model,
    "openai" => settings.OpenAI.Model,
    "ollama" => settings.Ollama.Model,
    _ => "unknown"
};

/// <summary>
/// Per-1M-token rates from the Chapter 3 pricing table (illustrative — verify
/// current rates before relying on them). Local models cost $0.
/// </summary>
static (decimal InputPer1M, decimal OutputPer1M) GetPricing(string model)
{
    string m = model.ToLowerInvariant();
    return m switch
    {
        _ when m.Contains("gpt-6-astra") => (10.00m, 50.00m),
        _ when m.Contains("gpt-5.6-sol") => (4.00m, 20.00m),
        _ when m.Contains("gpt-5.6-terra") => (2.00m, 12.00m),
        _ when m.Contains("gpt-5.6-luna") => (0.20m, 1.20m),
        _ => (0.00m, 0.00m)   // local (Ollama) or unknown — treat as free
    };
}

// ---------------------------------------------------------------
// Types
// ---------------------------------------------------------------

/// <summary>
/// One provider's answers across the whole question set.
/// </summary>
/// <param name="Provider">The provider name (also its keyed-service name).</param>
/// <param name="Model">The configured model id.</param>
/// <param name="Runs">One entry per answered question.</param>
/// <param name="Failure">The error message if the provider failed, else null.</param>
sealed record ProviderResult(
    string Provider,
    string Model,
    List<QuestionRun> Runs,
    string? Failure);

/// <summary>
/// Timing and token usage for a single question.
/// </summary>
/// <param name="Elapsed">Wall-clock latency for the call.</param>
/// <param name="InputTokens">Input tokens reported by the provider.</param>
/// <param name="OutputTokens">Output tokens reported by the provider.</param>
sealed record QuestionRun(TimeSpan Elapsed, long InputTokens, long OutputTokens);

// ---------------------------------------------------------------
// Settings — mirrors the companion AgenticAI.Common LlmSettings record
// ---------------------------------------------------------------
// Only the four chat-completion providers appear here: Foundry and Anthropic
// return an AIAgent, not an IChatClient (see Chapter 4), so they cannot join
// an IChatClient benchmark.

/// <summary>
/// Strongly-typed settings bound from the "LlmProvider" configuration section.
/// </summary>
sealed record LlmSettings
{
    /// <summary>The active provider for single-provider apps; the benchmark ignores this and runs every configured provider.</summary>
    public string Provider { get; init; } = "ollama";

    /// <summary>Azure OpenAI configuration.</summary>
    public AzureOpenAISettings AzureOpenAI { get; init; } = new();

    /// <summary>OpenAI configuration.</summary>
    public OpenAISettings OpenAI { get; init; } = new();

    /// <summary>Ollama (local) configuration.</summary>
    public OllamaSettings Ollama { get; init; } = new();
}

/// <summary>Azure OpenAI provider settings.</summary>
sealed record AzureOpenAISettings
{
    public string Endpoint { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "gpt-5.6-luna";
}

/// <summary>OpenAI provider settings.</summary>
sealed record OpenAISettings
{
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "gpt-5.6-luna";
}

/// <summary>Ollama (local) provider settings.</summary>
sealed record OllamaSettings
{
    public string Endpoint { get; init; } = "http://localhost:11434";
    public string Model { get; init; } = "llama3.2";
}
