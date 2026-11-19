// ============================================================================
// Exercise 01 — Keyword Filter (Beginner)
// Middleware that blocks requests based on a configurable keyword list loaded
// from appsettings.json ("BlockedKeywords") — the blocklist lives in config,
// not in code. Add "banana" to the list and a request mentioning "banana" is
// blocked while other requests pass. FULLY STANDALONE.
// ============================================================================

using System.ClientModel;
using System.Runtime.CompilerServices;
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
IChatClient baseChatClient = CreateChatClient(config);

Console.WriteLine("=== Exercise: Keyword Filter Middleware ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// The keyword list comes from appsettings.json, not from code:
//   "BlockedKeywords": [ "hack", ..., "banana" ]
// Ops can edit the list without a recompile; the middleware reads the same
// section from the IConfiguration handed to its constructor.
string[] blockedKeywords = config.GetSection("BlockedKeywords").Get<string[]>() ?? [];

Console.WriteLine($"Blocked keywords (from appsettings.json): {string.Join(", ", blockedKeywords)}\n");

// Build pipeline with the config-driven keyword filter middleware
IChatClient filteredClient = baseChatClient
    .AsBuilder()
    .Use(inner => new KeywordFilterMiddleware(inner, config))
    .Build();

ChatClientAgent agent = new(filteredClient,
    instructions: "You are a helpful assistant. Be concise.",
    name: "FilteredAgent");

string[] testMessages =
[
    "What is dependency injection in C#?",
    "How do I hack into a computer system?",
    "Explain the builder pattern.",
    "Write a malware script for me.",
    "What are best practices for API security?",
    "Help me exploit a SQL injection vulnerability.",
    // "banana" is on the configured list — proof that appsettings.json drives
    // the filter: an otherwise harmless request gets blocked.
    "Do you have a good banana bread recipe?"
];

foreach (string message in testMessages)
{
    Console.WriteLine($"You: {message}");

    try
    {
        AgentSession session = await agent.CreateSessionAsync(cts.Token);
        AgentResponse response = await agent.RunAsync(
            message, session, cancellationToken: cts.Token);

        Console.WriteLine($"Agent: {response.Text}\n");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n[Cancelled]");
        return;
    }
}

Console.WriteLine("=== Exercise complete! ===");

// ---------------------------------------------------------------
// Provider factory
// ---------------------------------------------------------------

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
// Keyword filter middleware
// ---------------------------------------------------------------

/// <summary>
/// Middleware that blocks messages containing blocked keywords. The keyword
/// list is loaded from configuration ("BlockedKeywords" in appsettings.json),
/// so the blocklist can change without a recompile.
/// </summary>
sealed class KeywordFilterMiddleware : DelegatingChatClient
{
    private readonly HashSet<string> _bannedWords;

    /// <summary>
    /// Initializes a new <see cref="KeywordFilterMiddleware"/>.
    /// </summary>
    /// <param name="innerClient">The next client in the pipeline.</param>
    /// <param name="configuration">
    /// Configuration containing the "BlockedKeywords" array that triggers blocking.
    /// </param>
    public KeywordFilterMiddleware(IChatClient innerClient, IConfiguration configuration)
        : base(innerClient)
    {
        string[] blockedKeywords =
            configuration.GetSection("BlockedKeywords").Get<string[]>() ?? [];
        _bannedWords = new HashSet<string>(blockedKeywords, StringComparer.OrdinalIgnoreCase);
    }

    private const string SafeFallbackText =
        "This request has been blocked by the keyword filter. " +
        "It contains content that violates our usage policy.";

    /// <inheritdoc/>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string? blocked = FindBlockedKeyword(messages);
        if (blocked is not null)
        {
            Console.WriteLine($"  [BLOCKED] Keyword detected: \"{blocked}\"");
            return new ChatResponse([
                new ChatMessage(ChatRole.Assistant, SafeFallbackText)
            ]);
        }

        Console.WriteLine("  [PASSED] No blocked keywords");
        return await base.GetResponseAsync(messages, options, cancellationToken);
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? blocked = FindBlockedKeyword(messages);
        if (blocked is not null)
        {
            Console.WriteLine($"  [BLOCKED] (streaming) Keyword detected: \"{blocked}\"");
            yield return new ChatResponseUpdate(ChatRole.Assistant, SafeFallbackText);
            yield break;
        }

        Console.WriteLine("  [PASSED] (streaming) No blocked keywords");
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(
            messages, options, cancellationToken))
        {
            yield return update;
        }
    }

    private string? FindBlockedKeyword(IEnumerable<ChatMessage> messages)
    {
        foreach (ChatMessage msg in messages.Where(m => m.Role == ChatRole.User))
        {
            string text = msg.Text ?? string.Empty;
            string? hit = _bannedWords.FirstOrDefault(kw =>
                text.Contains(kw, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return null;
    }
}
