// ============================================================================
// Exercise 02 — Response Validator (Intermediate)
// Post-response validator middleware: checks the model's output against a set
// of rules (no URLs, minimum length, maximum length, no unexpected code block
// in a short/casual response). When ANY rule fails, the response is REPLACED
// with a safe fallback message — never truncated — and the rule(s) that
// triggered are logged (rule id and lengths, never the response text). Both
// the blocking and streaming paths validate.
//
//   dotnet run                    live run against the configured provider
//   dotnet run -- --self-test     offline: one failing input per rule, RESULT n/n
// FULLY STANDALONE.
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

if (args.Contains("--self-test"))
    return await ValidatorSelfTest.RunAsync();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

using IHost host = builder.Build();
IConfiguration config = host.Services.GetRequiredService<IConfiguration>();
IChatClient baseChatClient = CreateChatClient(config);

Console.WriteLine("=== Exercise: Response Validator Middleware ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Build pipeline with the post-response validator
IChatClient validatedClient = baseChatClient
    .AsBuilder()
    .Use(inner => new ResponseValidatorMiddleware(
        inner, minResponseChars: 20, maxResponseChars: 500))
    .Build();

ChatClientAgent agent = new(validatedClient,
    instructions: "You are a helpful assistant. Always be concise.",
    name: "ValidatedAgent");

string[] prompts =
[
    "What is LINQ in one sentence?",
    // Elicits a long answer -> max-length rule fires -> safe fallback.
    "Write a detailed 2000-word essay about the history of computing.",
    // Elicits a URL -> no-urls rule fires -> safe fallback.
    "Where can I find the official .NET documentation? Give me the link.",
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
        return 1;
    }
}

Console.WriteLine("=== Exercise complete! ===");
return 0;

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
        "openai" => new OpenAI.OpenAIClient(new ApiKeyCredential(configuration["LlmProvider:OpenAI:ApiKey"] ?? throw new InvalidOperationException("Missing")))
            .GetChatClient(configuration["LlmProvider:OpenAI:Model"] ?? "gpt-5.6-luna").AsIChatClient(),
        "ollama" => new OllamaSharp.OllamaApiClient(new Uri(configuration["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"), configuration["LlmProvider:Ollama:Model"] ?? "llama3.2"),
        _ => throw new InvalidOperationException($"Unknown provider: '{provider}'")
    };
}

// ---------------------------------------------------------------
// Response validator middleware
// ---------------------------------------------------------------

/// <summary>
/// Post-response validator middleware. Calls the inner client, inspects the
/// response text against a rule set (no URLs, minimum length, maximum length,
/// no unexpected code block in a short/casual response), and — when any rule
/// fails — REPLACES the response with a safe fallback message. The violated
/// rule(s) are logged; the original text is never passed through, truncated,
/// or partially repaired.
/// </summary>
sealed partial class ResponseValidatorMiddleware : DelegatingChatClient
{
    private const string SafeFallbackText =
        "I can't share that response — it failed a post-response validation rule. " +
        "Please rephrase your request and try again.";

    private readonly int _minResponseChars;
    private readonly int _maxResponseChars;
    private readonly Action<string> _log;

    /// <summary>
    /// Initializes a new <see cref="ResponseValidatorMiddleware"/>.
    /// </summary>
    /// <param name="innerClient">The next client in the pipeline.</param>
    /// <param name="minResponseChars">Minimum acceptable response length in characters.</param>
    /// <param name="maxResponseChars">Maximum acceptable response length in characters.</param>
    public ResponseValidatorMiddleware(
        IChatClient innerClient, int minResponseChars, int maxResponseChars, Action<string>? log = null)
        : base(innerClient)
    {
        _minResponseChars = minResponseChars;
        _maxResponseChars = maxResponseChars;
        _log = log ?? Console.WriteLine;
    }

    public const string FallbackText = SafeFallbackText;

    /// <inheritdoc/>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ChatResponse response = await base.GetResponseAsync(messages, options, cancellationToken);

        string text = response.Text ?? string.Empty;
        List<string> violations = Validate(text);

        if (violations.Count == 0)
        {
            _log($"  [VALIDATION] Passed ({text.Length} chars)");
            return response;
        }

        LogViolations(violations, streaming: false);

        // ANY violated rule routes to the fallback — replace, never truncate.
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, SafeFallbackText));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Streaming validation needs the complete output before the rules can be
    /// evaluated, so this override buffers the stream, validates, and then
    /// emits either the buffered updates unchanged (all rules passed) or a
    /// single safe-fallback update (any rule failed).
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
        List<string> violations = Validate(text);

        if (violations.Count > 0)
        {
            LogViolations(violations, streaming: true);
            yield return new ChatResponseUpdate(ChatRole.Assistant, SafeFallbackText);
            yield break;
        }

        _log($"  [VALIDATION] (streaming) Passed ({text.Length} chars)");
        foreach (ChatResponseUpdate update in buffered)
        {
            yield return update;
        }
    }

    /// <summary>Runs every rule and returns the list of violations (empty when clean).</summary>
    internal List<string> Validate(string text)
    {
        List<string> violations = [];

        // Rule 1: no URLs — the agent must not hand out unvetted links.
        if (UrlRegex().IsMatch(text))
        {
            violations.Add("no-urls: response contains a URL");
        }

        // Rule 2: minimum length — one-word non-answers are not acceptable.
        if (text.Length < _minResponseChars)
        {
            violations.Add($"min-length: {text.Length} chars (min {_minResponseChars})");
        }

        // Rule 3: maximum length — runaway essays get replaced, not truncated.
        if (text.Length > _maxResponseChars)
        {
            violations.Add($"max-length: {text.Length} chars (max {_maxResponseChars})");
        }

        // Rule 4: no unexpected code block in a short/casual response.
        if (text.Contains("```", StringComparison.Ordinal) && text.Length < 200)
        {
            violations.Add("unexpected-code-block: code fences in a short/casual response");
        }

        return violations;
    }

    /// <summary>Logs the rule(s) that triggered — the shape ContentFilterMiddleware uses.</summary>
    private void LogViolations(List<string> violations, bool streaming)
    {
        string suffix = streaming ? " (streaming)" : string.Empty;
        _log(
            $"  [VALIDATION]{suffix} {violations.Count} rule(s) violated -> " +
            "response replaced with safe fallback:");
        foreach (string violation in violations)
        {
            _log($"    - {violation}");
        }
    }

    [GeneratedRegex(@"(?i)\bhttps?://\S+|\bwww\.\S+")]
    private static partial Regex UrlRegex();
}

/// <summary>A model that replies with fixed text, as one update per chunk when streaming.</summary>
sealed class FixedReplyClient(params string[] chunks) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Concat(chunks))));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (string chunk in chunks)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

static class ValidatorSelfTest
{
    // Every bad response carries this marker; it must never reach the log.
    const string Marker = "zebra4711";

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Exercise: Response Validator: self-test ===");
        int passed = 0, total = 0;
        void Expect(string name, bool ok)
        {
            total++;
            if (ok) passed++;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        }

        Console.WriteLine("\n-- One failing input per rule --");
        (string Rule, string Reply)[] cases =
        [
            ("no-urls", $"The {Marker} guide is at https://contoso.example/docs for all the details."),
            ("no-urls", $"The {Marker} guide is at www.contoso.example for all the details you need."),
            ("min-length", $"{Marker}."),
            ("max-length", $"{Marker} " + new string('x', 600)),
            ("unexpected-code-block", $"Use ```var {Marker} = 1;``` in your code, that is all."),
        ];
        foreach ((string rule, string reply) in cases)
        {
            List<string> log = [];
            IChatClient client = new ResponseValidatorMiddleware(new FixedReplyClient(reply), 20, 500, log.Add);
            ChatResponse r = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "q")]);
            Expect($"{rule}: replaced by the fallback, rule logged, text not logged",
                r.Text == ResponseValidatorMiddleware.FallbackText &&
                log.Any(l => l.Contains(rule, StringComparison.Ordinal)) &&
                log.All(l => !l.Contains(Marker, StringComparison.Ordinal)));
        }

        Console.WriteLine("\n-- Clean answers pass unchanged --");
        {
            const string clean = "LINQ is a set of query operators for in-memory and remote sequences.";
            IChatClient client = new ResponseValidatorMiddleware(new FixedReplyClient(clean), 20, 500, _ => { });
            ChatResponse r = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "q")]);
            Expect("a clean answer is returned as it came", r.Text == clean);
            string longCode = "Here is a longer example.\n```csharp\n" + new string('/', 220) + "\n```";
            Expect("a code block in a long answer is allowed",
                (await new ResponseValidatorMiddleware(new FixedReplyClient(longCode), 20, 500, _ => { })
                    .GetResponseAsync([new ChatMessage(ChatRole.User, "q")])).Text == longCode);
        }

        Console.WriteLine("\n-- Streaming: validated on the whole text --");
        {
            List<string> log = [];
            IChatClient client = new ResponseValidatorMiddleware(
                new FixedReplyClient($"The {Marker} docs live at htt", "ps://contoso.example/docs, have a look."), 20, 500, log.Add);
            List<string> texts = [];
            await foreach (ChatResponseUpdate u in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "q")]))
                texts.Add(u.Text);
            Expect("a URL split across two chunks is caught; one fallback update is emitted",
                texts.Count == 1 && texts[0] == ResponseValidatorMiddleware.FallbackText && log.All(l => !l.Contains(Marker, StringComparison.Ordinal)));
        }

        Console.WriteLine($"\nRESULT {passed}/{total}");
        return passed == total ? 0 : 1;
    }
}
