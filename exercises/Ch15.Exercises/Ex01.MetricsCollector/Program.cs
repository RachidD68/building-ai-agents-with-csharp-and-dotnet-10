// ============================================================================
// Exercise 01 — Metrics Collector (Beginner)
// Extends Ch15.OpenTelemetryAgent with ONE new metric: agent.cache.hit_rate,
// a Counter<long> tagged with cache.name, emitted from a simulated response
// cache inside the agent pipeline. 100 requests over a small question set
// drive the hits and misses; the OpenTelemetry console exporter shows the
// metric at shutdown. Per the hint: increment on hit, don't increment on
// miss, and filter by the cache.name tag in the exporter output.
// FULLY STANDALONE.
// ============================================================================

using System.ClientModel;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;

// Same meter name as src/Ch15.Observability's AgentMetrics — the new counter
// extends that meter family; the exercise stays standalone by creating the
// Meter locally instead of referencing the src project.
const string MeterName = "AgenticAI.Agent";
const string CacheName = "response-cache";
const int TotalRequests = 100;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// Metrics pipeline mirrors Ch15.OpenTelemetryAgent's zero-config default:
// subscribe to our meter, export to the console. (The src project also adds
// HTTP-client instrumentation; omitted here so the cache metric is easy to
// spot in the exporter dump.)
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter(MeterName)
        .AddConsoleExporter());

using IHost host = builder.Build();

// Start the host so the OpenTelemetry hosted service installs its
// MeterListener. Without this, the counter records into the void and the
// exporter stays silent — the empty-dashboard failure Chapter 15 warns about.
await host.StartAsync();

IConfiguration config = host.Services.GetRequiredService<IConfiguration>();
IChatClient chatClient = CreateChatClient(config);

Console.WriteLine("=== Exercise: Metrics Collector (agent.cache.hit_rate) ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// One new metric — exactly one, as the brief asks: a Counter<long> named
// agent.cache.hit_rate, tagged with cache.name. Incremented on hit only.
using Meter meter = new(MeterName, "1.0.0");
Counter<long> cacheHitCounter = meter.CreateCounter<long>(
    "agent.cache.hit_rate",
    unit: "hits",
    description: "Response-cache hits (incremented on hit only), tagged with cache.name");

// The simulated cache lives inside the agent pipeline: a DelegatingChatClient
// keyed on the user prompt. Only unique prompts reach the model; repeats are
// served from memory and count as cache hits.
ResponseCachingChatClient cachedClient = new(chatClient, cacheHitCounter, CacheName);

ChatClientAgent agent = new(cachedClient,
    instructions: "You are a helpful assistant. Be concise (2-3 sentences max).",
    name: "CachedAgent");

// 10 unique questions x 10 rounds = 100 requests. The first pass over the set
// misses (those prompts hit the model); the remaining 90 requests hit the
// cache, so the final hit rate lands at 90%.
string[] questions =
[
    "What is C#?",
    "Explain async/await in one sentence.",
    "What is dependency injection?",
    "Name 3 SOLID principles.",
    "What is the difference between struct and class?",
    "What is LINQ?",
    "What does the using statement do?",
    "What is a record type in C#?",
    "What is garbage collection?",
    "What is the purpose of NuGet?"
];

Console.WriteLine($"Running {TotalRequests} requests over {questions.Length} unique questions...\n");

try
{
    for (int i = 0; i < TotalRequests; i++)
    {
        string prompt = questions[i % questions.Length];

        AgentSession session = await agent.CreateSessionAsync(cts.Token);
        await agent.RunAsync(prompt, session, cancellationToken: cts.Token);

        if ((i + 1) % 25 == 0)
        {
            Console.WriteLine($"  ... {i + 1}/{TotalRequests} requests processed " +
                              $"(hits so far: {cachedClient.Hits})");
        }
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled]");
}

// ---------------------------------------------------------------
// Local summary — the metric itself is flushed by the exporter below.
// ---------------------------------------------------------------
double hitRate = cachedClient.Hits + cachedClient.Misses == 0
    ? 0.0
    : (double)cachedClient.Hits / (cachedClient.Hits + cachedClient.Misses);

Console.WriteLine();
Console.WriteLine(new string('=', 60));
Console.WriteLine("=== Cache Summary ===\n");
Console.WriteLine($"  Total requests : {cachedClient.Hits + cachedClient.Misses}");
Console.WriteLine($"  Unique prompts : {cachedClient.UniqueEntries}");
Console.WriteLine($"  Cache hits     : {cachedClient.Hits}");
Console.WriteLine($"  Cache misses   : {cachedClient.Misses}");
Console.WriteLine($"  Hit rate       : {hitRate:P1}");
Console.WriteLine();
Console.WriteLine("Stopping the host — the console exporter flushes agent.cache.hit_rate");
Console.WriteLine($"below. Filter for the cache.name = \"{CacheName}\" tag on the metric points.");

// Stop the host so the meter provider flushes its final batch to the console
// exporter before the process exits.
await host.StopAsync();

Console.WriteLine("\n=== Exercise complete! ===");

/// <summary>Creates an IChatClient from configuration.</summary>
static IChatClient CreateChatClient(IConfiguration configuration)
{
    string provider = configuration["LlmProvider:Provider"]?.ToLowerInvariant() ?? "ollama";
    return provider switch
    {
        "azure" => new AzureOpenAIClient(new Uri(configuration["LlmProvider:AzureOpenAI:Endpoint"] ?? throw new InvalidOperationException("Missing")), new DefaultAzureCredential())
            .GetChatClient(configuration["LlmProvider:AzureOpenAI:Model"] ?? "gpt-5.6-luna").AsIChatClient(),
        "openai" => new OpenAI.OpenAIClient(configuration["LlmProvider:OpenAI:ApiKey"] ?? throw new InvalidOperationException("Missing"))
            .GetChatClient(configuration["LlmProvider:OpenAI:Model"] ?? "gpt-5.6-luna").AsIChatClient(),
        "ollama" => new OllamaSharp.OllamaApiClient(new Uri(configuration["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"), configuration["LlmProvider:Ollama:Model"] ?? "llama3.2"),
        _ => throw new InvalidOperationException($"Unknown provider: '{provider}'")
    };
}

// ---------------------------------------------------------------
// Simulated response cache (the agent pipeline's cache layer)
// ---------------------------------------------------------------

/// <summary>
/// A simulated response cache inside the agent pipeline. Repeated prompts are
/// served from memory (a hit — no model call); unique prompts forward to the
/// inner client (a miss) and their responses are stored. Every hit increments
/// the agent.cache.hit_rate counter with a cache.name tag; misses do not
/// increment (the hint's contract), so the exporter shows pure hit counts.
/// </summary>
sealed class ResponseCachingChatClient : DelegatingChatClient
{
    private readonly Counter<long> _hitCounter;
    private readonly string _cacheName;
    private readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of requests served from the cache.</summary>
    public int Hits { get; private set; }

    /// <summary>Number of requests forwarded to the model.</summary>
    public int Misses { get; private set; }

    /// <summary>Number of distinct prompts stored in the cache.</summary>
    public int UniqueEntries => _cache.Count;

    /// <summary>
    /// Initializes a new <see cref="ResponseCachingChatClient"/>.
    /// </summary>
    /// <param name="innerClient">The next client in the pipeline.</param>
    /// <param name="hitCounter">The agent.cache.hit_rate counter (incremented on hit only).</param>
    /// <param name="cacheName">Value for the cache.name tag on every measurement.</param>
    public ResponseCachingChatClient(
        IChatClient innerClient, Counter<long> hitCounter, string cacheName)
        : base(innerClient)
    {
        _hitCounter = hitCounter;
        _cacheName = cacheName;
    }

    /// <inheritdoc/>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string key = CacheKey(messages);
        if (_cache.TryGetValue(key, out string? cachedText))
        {
            Hits++;
            // Increment on hit; never on miss.
            _hitCounter.Add(1, new KeyValuePair<string, object?>("cache.name", _cacheName));
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, cachedText));
        }

        Misses++;
        ChatResponse response = await base.GetResponseAsync(messages, options, cancellationToken);
        string text = response.Text ?? string.Empty;
        _cache[key] = text;
        Console.WriteLine($"  [cache miss] \"{Truncate(key, 44)}\" -> model ({text.Length} chars)");
        return response;
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string key = CacheKey(messages);
        if (_cache.TryGetValue(key, out string? cachedText))
        {
            Hits++;
            _hitCounter.Add(1, new KeyValuePair<string, object?>("cache.name", _cacheName));
            yield return new ChatResponseUpdate(ChatRole.Assistant, cachedText);
            yield break;
        }

        Misses++;
        StringBuilder buffer = new();
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(
            messages, options, cancellationToken))
        {
            if (update.Text is { Length: > 0 } chunk)
            {
                buffer.Append(chunk);
            }
            yield return update;
        }

        _cache[key] = buffer.ToString();
    }

    private static string CacheKey(IEnumerable<ChatMessage> messages)
        => messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..(max - 3)] + "...";
}
