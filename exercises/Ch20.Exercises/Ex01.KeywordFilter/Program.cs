// ============================================================================
// Exercise 01 - Keyword Filter (Basic)
// Middleware that blocks requests whose user text contains a keyword from
// configuration ("BlockedKeywords" in appsettings.json): the list lives in
// config, not in code. Add "banana" and only requests mentioning a banana are
// blocked. The text is normalised before matching, so upper case, full-width
// letters, zero-width characters and common Cyrillic/Greek look-alikes do not
// slip past. It remains a signal, never the decision for an action.
//
//   dotnet run                    live run against the configured provider
//   dotnet run -- --self-test     offline: scripted model, RESULT n/n
// FULLY STANDALONE.
// ============================================================================

using System.ClientModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

IConfiguration config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables()
    .Build();
string[] blockedKeywords = config.GetSection("BlockedKeywords").Get<string[]>() ?? [];

if (args.Contains("--self-test"))
    return await KeywordSelfTest.RunAsync(blockedKeywords);

Console.WriteLine("=== Exercise: Keyword Filter Middleware ===\n");
Console.WriteLine($"Blocked keywords (from appsettings.json): {string.Join(", ", blockedKeywords)}\n");

IChatClient filtered = CreateChatClient(config).AsBuilder()
    .Use(inner => new KeywordFilterMiddleware(inner, blockedKeywords))
    .Build();
ChatClientAgent agent = new(filtered, instructions: "You are a helpful assistant. Be concise.", name: "FilteredAgent");

string[] testMessages =
[
    "What is dependency injection in C#?",
    "How do I hack into a computer system?",
    "Explain the builder pattern.",
    "Do you have a good banana bread recipe?",
];

foreach (string message in testMessages)
{
    Console.WriteLine($"You: {message}");
    AgentResponse response = await agent.RunAsync(message);
    Console.WriteLine($"Agent: {response.Text}\n");
}
return 0;

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
        _ => throw new InvalidOperationException($"Unknown provider: '{provider}'"),
    };
}

/// <summary>Blocks a request whose normalised user text contains a configured keyword.</summary>
sealed class KeywordFilterMiddleware(IChatClient innerClient, IEnumerable<string> blockedKeywords) : DelegatingChatClient(innerClient)
{
    public const string SafeFallbackText = "This request has been blocked by the keyword filter.";
    private readonly string[] _keywords = [.. blockedKeywords.Select(TextFold.Normalize).Where(k => k.Length > 0).Distinct()];

    public string? FindBlockedKeyword(IEnumerable<ChatMessage> messages) =>
        messages.Where(m => m.Role == ChatRole.User)
            .Select(m => TextFold.Normalize(m.Text))
            .SelectMany(text => _keywords.Where(k => text.Contains(k, StringComparison.Ordinal)))
            .FirstOrDefault();

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        List<ChatMessage> sent = [.. messages];
        return FindBlockedKeyword(sent) is not null
            ? new ChatResponse(new ChatMessage(ChatRole.Assistant, SafeFallbackText)) { FinishReason = ChatFinishReason.ContentFilter }
            : await base.GetResponseAsync(sent, options, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<ChatMessage> sent = [.. messages];
        if (FindBlockedKeyword(sent) is not null)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, SafeFallbackText) { FinishReason = ChatFinishReason.ContentFilter };
            yield break;
        }
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(sent, options, cancellationToken))
            yield return update;
    }
}

/// <summary>
/// One comparable form: NFKC (full-width and compatibility forms), format
/// characters removed (zero-width space, joiners), a small map of Cyrillic and
/// Greek look-alikes folded to Latin, then lower case (invariant).
/// </summary>
static class TextFold
{
    private static readonly Dictionary<char, char> Confusables = new()
    {
        ['а'] = 'a', ['е'] = 'e', ['о'] = 'o', ['р'] = 'p', ['с'] = 'c',
        ['у'] = 'y', ['х'] = 'x', ['і'] = 'i', ['А'] = 'a', ['Е'] = 'e',
        ['О'] = 'o', ['Р'] = 'p', ['С'] = 'c', ['Х'] = 'x', ['ο'] = 'o',
        ['α'] = 'a', ['Ο'] = 'o', ['Α'] = 'a', ['Β'] = 'b', ['Ν'] = 'n',
    };

    public static string Normalize(string text)
    {
        StringBuilder folded = new(text.Length);
        foreach (char c in text.Normalize(NormalizationForm.FormKC))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;
            folded.Append(Confusables.TryGetValue(c, out char latin) ? latin : c);
        }
        return folded.ToString().ToLowerInvariant();
    }
}

/// <summary>A model that answers "ok" and counts calls.</summary>
sealed class CountingClient : IChatClient
{
    public int Calls;
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
    }
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Calls);
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
    }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

static class KeywordSelfTest
{
    public static async Task<int> RunAsync(string[] configured)
    {
        Console.WriteLine("=== Exercise: Keyword Filter: self-test ===");
        int passed = 0, total = 0;
        void Expect(string name, bool ok)
        {
            total++;
            if (ok) passed++;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        }

        async Task<bool> Blocked(string[] keywords, string text)
        {
            CountingClient model = new();
            IChatClient client = new KeywordFilterMiddleware(model, keywords);
            ChatResponse r = await client.GetResponseAsync([new ChatMessage(ChatRole.User, text)]);
            return model.Calls == 0 && r.FinishReason == ChatFinishReason.ContentFilter;
        }

        Console.WriteLine($"\n-- From appsettings.json: {string.Join(", ", configured)} --");
        Expect("\"banana\" is in the configured list", configured.Contains("banana"));
        Expect("a banana bread question is blocked", await Blocked(configured, "Do you have a good banana bread recipe?"));
        Expect("an apple pie question passes", !await Blocked(configured, "Do you have a good apple pie recipe?"));
        string[] withoutBanana = [.. configured.Where(k => k != "banana")];
        Expect("without \"banana\" in config, the same banana question passes", !await Blocked(withoutBanana, "Do you have a good banana bread recipe?"));

        Console.WriteLine("\n-- Variants of \"banana\" --");
        (string Text, string Label)[] variants =
        [
            ("I want a BANANA.", "upper case"),
            ("I want a Banana.", "title case"),
            ("I want a bаnаnа.", "Cyrillic a (U+0430)"),
            ("I want a b​anana.", "zero-width space (U+200B)"),
            ("I want a ｂａｎａｎａ.", "full-width letters (NFKC)"),
            ("I want a bαnαnα.", "Greek alpha (U+03B1)"),
        ];
        foreach ((string text, string label) in variants)
            Expect($"blocked: {label}", await Blocked(["banana"], text));
        Expect("not blocked: \"bandana\"", !await Blocked(["banana"], "I wear a bandana."));
        Expect("only user messages are scanned", !await BlockedSystem());

        Console.WriteLine("\n-- Streaming path --");
        {
            CountingClient model = new();
            IChatClient client = new KeywordFilterMiddleware(model, ["banana"]);
            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate u in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "BANANA please")]))
                updates.Add(u);
            Expect("blocked on the streaming path too, the model is not called",
                model.Calls == 0 && updates.Any(u => u.FinishReason == ChatFinishReason.ContentFilter));
        }

        Console.WriteLine($"\nRESULT {passed}/{total}");
        return passed == total ? 0 : 1;

        static async Task<bool> BlockedSystem()
        {
            CountingClient model = new();
            await new KeywordFilterMiddleware(model, ["banana"]).GetResponseAsync(
                [new ChatMessage(ChatRole.System, "Never talk about banana prices."), new ChatMessage(ChatRole.User, "Hello")]);
            return model.Calls == 0;
        }
    }
}
