// ============================================================================
// Exercise 02 — Response Validator (Intermediate)
// Middleware that validates agent responses against rules (must contain
// certain markers, must not exceed a token limit). FULLY STANDALONE.
// ============================================================================

using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Text;
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

Console.WriteLine("=== Exercise: Response Validator Middleware ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Build pipeline with response validator
IChatClient validatedClient = baseChatClient
    .AsBuilder()
    .Use(inner => new ResponseValidatorMiddleware(inner, maxResponseChars: 500))
    .Build();

ChatClientAgent agent = new(validatedClient,
    instructions: "You are a helpful assistant. Always be concise.",
    name: "ValidatedAgent");

string[] prompts =
[
    "What is LINQ in one sentence?",
    "Write a detailed 2000-word essay about the history of computing.",
    "What are the SOLID principles? Explain each briefly.",
    "Tell me a very short joke about programming."
];

foreach (string prompt in prompts)
{
    Console.WriteLine($"You: {prompt}");

    try
    {
        AgentSession session = await agent.CreateSessionAsync(cts.Token);
        AgentResponse response = await agent.RunAsync(
            prompt, session, cancellationToken: cts.Token);

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
// Response validator middleware
// ---------------------------------------------------------------

/// <summary>
/// Middleware that validates responses against configurable rules:
/// max character length and content requirements.
/// </summary>
sealed class ResponseValidatorMiddleware : DelegatingChatClient
{
    private readonly int _maxResponseChars;

    /// <summary>
    /// Initializes a new <see cref="ResponseValidatorMiddleware"/>.
    /// </summary>
    /// <param name="innerClient">The next client in the pipeline.</param>
    /// <param name="maxResponseChars">Maximum allowed response length in characters.</param>
    public ResponseValidatorMiddleware(IChatClient innerClient, int maxResponseChars)
        : base(innerClient)
    {
        _maxResponseChars = maxResponseChars;
    }

    /// <inheritdoc/>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ChatResponse response = await base.GetResponseAsync(messages, options, cancellationToken);

        string text = response.Text ?? string.Empty;
        List<string> violations = [];

        // Rule 1: Max character length
        if (text.Length > _maxResponseChars)
        {
            violations.Add($"Response too long: {text.Length} chars (max {_maxResponseChars})");
        }

        // Rule 2: Should not contain code blocks in casual responses
        // (just as a validation example)
        if (text.Contains("```") && text.Length < 50)
        {
            violations.Add("Unexpected code block in short response");
        }

        if (violations.Count > 0)
        {
            Console.WriteLine($"  [VALIDATION] {violations.Count} violation(s):");
            foreach (string v in violations)
            {
                Console.WriteLine($"    - {v}");
            }

            // Truncate if too long
            if (text.Length > _maxResponseChars)
            {
                string truncated = text[.._maxResponseChars] + "... [TRUNCATED by validator]";
                return new ChatResponse([
                    new ChatMessage(ChatRole.Assistant, truncated)
                ]);
            }
        }
        else
        {
            Console.WriteLine($"  [VALIDATION] Passed ({text.Length} chars)");
        }

        return response;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Streaming validation requires the complete output to evaluate length and
    /// content rules, so this implementation buffers the stream, validates, and
    /// then emits either the original updates or a single truncated replacement.
    /// True token-by-token streaming validation is possible (truncate as soon as
    /// the buffer exceeds the limit) but adds complexity beyond this exercise.
    /// </remarks>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
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
        List<string> violations = [];
        if (text.Length > _maxResponseChars)
        {
            violations.Add($"Response too long: {text.Length} chars (max {_maxResponseChars})");
        }
        if (text.Contains("```") && text.Length < 50)
        {
            violations.Add("Unexpected code block in short response");
        }

        if (violations.Count > 0)
        {
            Console.WriteLine($"  [VALIDATION] (streaming) {violations.Count} violation(s):");
            foreach (string v in violations)
            {
                Console.WriteLine($"    - {v}");
            }

            if (text.Length > _maxResponseChars)
            {
                string truncated = text[.._maxResponseChars] + "... [TRUNCATED by validator]";
                yield return new ChatResponseUpdate(ChatRole.Assistant, truncated);
                yield break;
            }
        }
        else
        {
            Console.WriteLine($"  [VALIDATION] (streaming) Passed ({text.Length} chars)");
        }

        foreach (ChatResponseUpdate u in buffered)
        {
            yield return u;
        }
    }
}
