// ============================================================================
// Exercise 03 — Full Safety Pipeline (Challenge)
// Combines input sanitization + content filter + PII redaction + response
// length guard into a full safety pipeline. FULLY STANDALONE.
// ============================================================================

using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
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

Console.WriteLine("=== Exercise: Full Safety Pipeline ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

string[] bannedKeywords = ["hack", "exploit", "malware"];

// Build the full safety pipeline (innermost → outermost)
IChatClient safePipeline = baseChatClient
    .AsBuilder()
    .Use(inner => new ResponseLengthGuard(inner, maxChars: 800))
    .Use(inner => new PiiRedactionMiddleware(inner))
    .Use(inner => new ContentFilterMiddleware(inner, bannedKeywords))
    .Use(inner => new InputSanitizer(inner))
    .Build();

ChatClientAgent agent = new(safePipeline,
    instructions: "You are a helpful assistant. Be concise.",
    name: "SafeAgent");

Console.WriteLine("Pipeline layers (request order):");
Console.WriteLine("  1. Input Sanitizer (trim, normalize whitespace)");
Console.WriteLine("  2. Content Filter (block banned keywords)");
Console.WriteLine("  3. PII Redaction (mask emails, phones, SSNs)");
Console.WriteLine("  4. Response Length Guard (truncate long responses)");
Console.WriteLine();

string[] testMessages =
[
    "  What   is   dependency   injection?  ",
    "My email is john@example.com and my phone is 555-123-4567.",
    "How do I hack into a database?",
    "My SSN is 123-45-6789. Can you remember it?",
    "Write a very brief explanation of async/await."
];

foreach (string message in testMessages)
{
    Console.WriteLine($"You: \"{message}\"");
    Console.WriteLine("  Processing through safety pipeline...");

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

// ---------------------------------------------------------------
// Middleware: Input Sanitizer
// ---------------------------------------------------------------

/// <summary>Normalizes whitespace and trims input messages.</summary>
sealed partial class InputSanitizer : DelegatingChatClient
{
    /// <summary>Creates a new <see cref="InputSanitizer"/>.</summary>
    public InputSanitizer(IChatClient inner) : base(inner) { }

    /// <inheritdoc/>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        List<ChatMessage> sanitized = Sanitize(messages);
        return await base.GetResponseAsync(sanitized, options, cancellationToken);
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<ChatMessage> sanitized = Sanitize(messages);
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(
            sanitized, options, cancellationToken))
        {
            yield return update;
        }
    }

    private static List<ChatMessage> Sanitize(IEnumerable<ChatMessage> messages)
    {
        List<ChatMessage> sanitized = [];
        foreach (ChatMessage msg in messages)
        {
            if (msg.Role == ChatRole.User && msg.Text is not null)
            {
                string clean = WhitespaceRegex().Replace(msg.Text.Trim(), " ");
                Console.WriteLine($"    [Sanitizer] Cleaned: \"{clean}\"");
                sanitized.Add(new ChatMessage(msg.Role, clean));
            }
            else
            {
                sanitized.Add(msg);
            }
        }
        return sanitized;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

// ---------------------------------------------------------------
// Middleware: Content Filter
// ---------------------------------------------------------------

/// <summary>Blocks messages with banned keywords (every user message, both invocation paths).</summary>
sealed class ContentFilterMiddleware : DelegatingChatClient
{
    private const string SafeFallbackText = "Request blocked by content filter.";
    private readonly HashSet<string> _banned;

    /// <summary>Creates a new <see cref="ContentFilterMiddleware"/>.</summary>
    public ContentFilterMiddleware(IChatClient inner, IEnumerable<string> banned) : base(inner)
    {
        _banned = new(banned, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string? found = FindBanned(messages);
        if (found is not null)
        {
            Console.WriteLine($"    [ContentFilter] BLOCKED: \"{found}\"");
            return new ChatResponse([new ChatMessage(ChatRole.Assistant, SafeFallbackText)]);
        }
        Console.WriteLine("    [ContentFilter] Passed");
        return await base.GetResponseAsync(messages, options, cancellationToken);
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? found = FindBanned(messages);
        if (found is not null)
        {
            Console.WriteLine($"    [ContentFilter] (streaming) BLOCKED: \"{found}\"");
            yield return new ChatResponseUpdate(ChatRole.Assistant, SafeFallbackText);
            yield break;
        }
        Console.WriteLine("    [ContentFilter] (streaming) Passed");
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(
            messages, options, cancellationToken))
        {
            yield return update;
        }
    }

    private string? FindBanned(IEnumerable<ChatMessage> messages)
    {
        foreach (ChatMessage msg in messages.Where(m => m.Role == ChatRole.User))
        {
            string text = msg.Text ?? string.Empty;
            string? hit = _banned.FirstOrDefault(w =>
                text.Contains(w, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return null;
    }
}

// ---------------------------------------------------------------
// Middleware: PII Redaction
// ---------------------------------------------------------------

/// <summary>Redacts PII (emails, phone numbers, SSNs) from input on both invocation paths.</summary>
sealed partial class PiiRedactionMiddleware : DelegatingChatClient
{
    /// <summary>Creates a new <see cref="PiiRedactionMiddleware"/>.</summary>
    public PiiRedactionMiddleware(IChatClient inner) : base(inner) { }

    /// <inheritdoc/>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        List<ChatMessage> redacted = Redact(messages);
        return await base.GetResponseAsync(redacted, options, cancellationToken);
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<ChatMessage> redacted = Redact(messages);
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(
            redacted, options, cancellationToken))
        {
            yield return update;
        }
    }

    private static List<ChatMessage> Redact(IEnumerable<ChatMessage> messages)
    {
        List<ChatMessage> redacted = [];
        foreach (ChatMessage msg in messages)
        {
            if (msg.Role == ChatRole.User && msg.Text is not null)
            {
                string text = msg.Text;
                text = EmailRegex().Replace(text, "[EMAIL REDACTED]");
                text = PhoneRegex().Replace(text, "[PHONE REDACTED]");
                text = SsnRegex().Replace(text, "[SSN REDACTED]");

                if (text != msg.Text)
                {
                    Console.WriteLine($"    [PII Redaction] Redacted PII: \"{text}\"");
                }
                else
                {
                    Console.WriteLine("    [PII Redaction] No PII found");
                }
                redacted.Add(new ChatMessage(msg.Role, text));
            }
            else
            {
                redacted.Add(msg);
            }
        }
        return redacted;
    }

    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.]+")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\b\d{3}[-.]?\d{3}[-.]?\d{4}\b")]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b")]
    private static partial Regex SsnRegex();
}

// ---------------------------------------------------------------
// Middleware: Response Length Guard
// ---------------------------------------------------------------

/// <summary>Truncates responses that exceed maximum length on both invocation paths.</summary>
sealed class ResponseLengthGuard : DelegatingChatClient
{
    private readonly int _maxChars;

    /// <summary>Creates a new <see cref="ResponseLengthGuard"/>.</summary>
    public ResponseLengthGuard(IChatClient inner, int maxChars) : base(inner)
    {
        _maxChars = maxChars;
    }

    /// <inheritdoc/>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ChatResponse response = await base.GetResponseAsync(messages, options, cancellationToken);
        string text = response.Text ?? "";
        if (text.Length > _maxChars)
        {
            Console.WriteLine($"    [LengthGuard] Truncated: {text.Length} → {_maxChars} chars");
            return new ChatResponse([new ChatMessage(ChatRole.Assistant, text[.._maxChars] + "...")]);
        }
        Console.WriteLine($"    [LengthGuard] OK ({text.Length} chars)");
        return response;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Streaming length-guard buffers the stream, evaluates total length, and
    /// either replays the original updates or emits a single truncated update.
    /// True token-by-token enforcement (cut off mid-stream once the limit is
    /// reached) is also valid; this version preserves the simpler post-validation
    /// shape for the exercise.
    /// </remarks>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<ChatResponseUpdate> buffered = [];
        StringBuilder textBuffer = new();

        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(
            messages, options, cancellationToken))
        {
            buffered.Add(update);
            if (update.Text is { Length: > 0 } chunk)
            {
                textBuffer.Append(chunk);
            }
        }

        string text = textBuffer.ToString();
        if (text.Length > _maxChars)
        {
            Console.WriteLine($"    [LengthGuard] (streaming) Truncated: {text.Length} → {_maxChars} chars");
            yield return new ChatResponseUpdate(
                ChatRole.Assistant, text[.._maxChars] + "...");
            yield break;
        }

        Console.WriteLine($"    [LengthGuard] (streaming) OK ({text.Length} chars)");
        foreach (ChatResponseUpdate u in buffered)
        {
            yield return u;
        }
    }
}
