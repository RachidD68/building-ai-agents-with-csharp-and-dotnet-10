using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace Ex01.MultiProviderChat;

/// <summary>Offline checks of the switch. Building a client opens no connection.</summary>
internal static class SelfTest
{
    const string Placeholder = "sk-placeholder-0000";
    const string EnvPrefix = "EX01SELFTEST_";
    static int s_passed, s_total;

    public static int Run()
    {
        Console.WriteLine("=== Multi-provider chat: self-test (no model is called) ===\n");

        IConfiguration none = Memory();
        Expect("nothing configured -> ollama, from the built-in default",
            ProviderSwitch.Describe(none) == "Provider: ollama   (from the built-in default)");
        Expect("ollama builds an Ollama client", ProviderName(ProviderSwitch.Create(none)) == "ollama");

        Environment.SetEnvironmentVariable(EnvPrefix + "LlmProvider__Provider", "openai");
        Environment.SetEnvironmentVariable(EnvPrefix + "LlmProvider__OpenAI__ApiKey", Placeholder);
        try
        {
            IConfigurationRoot switched = new ConfigurationBuilder()
                .AddInMemoryCollection(Pairs([("LlmProvider:Provider", "ollama"), ("LlmProvider:OpenAI:Model", "gpt-5.6-luna")]))
                .AddEnvironmentVariables(EnvPrefix)
                .Build();
            Expect("an environment variable switches to openai, no code change",
                ProviderSwitch.Describe(switched) == "Provider: openai   (from environment variables)");
            Expect("openai builds an OpenAI client", ProviderName(ProviderSwitch.Create(switched)) == "openai");
            Expect("--config-check prints no key", !ProviderSwitch.Describe(switched).Contains(Placeholder));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvPrefix + "LlmProvider__Provider", null);
            Environment.SetEnvironmentVariable(EnvPrefix + "LlmProvider__OpenAI__ApiKey", null);
        }

        Expect("openai with an empty ApiKey -> the error names LlmProvider:OpenAI:ApiKey",
            Error(Memory(("LlmProvider:Provider", "openai"), ("LlmProvider:OpenAI:ApiKey", ""))) is { } e1
            && e1.Contains("LlmProvider:OpenAI:ApiKey"));
        Expect("azure without an endpoint -> the error names LlmProvider:AzureOpenAI:Endpoint",
            Error(Memory(("LlmProvider:Provider", "azure"))) is { } e2 && e2.Contains("LlmProvider:AzureOpenAI:Endpoint"));
        Expect("unknown provider -> the error lists the valid values",
            Error(Memory(("LlmProvider:Provider", "gemini"))) is { } e3 && e3.Contains("ollama, openai, azure"));

        string turn = ChatTurn.AskAsync(new FakeChatClient(), "What is LINQ?").GetAwaiter().GetResult();
        Expect("a chat turn over the fake client prints the answer and the tokens",
            turn == "Assistant: fake answer to: What is LINQ?\n  [tokens: 3 in, 5 out]");

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    static string? ProviderName(IChatClient client) => client.GetService<ChatClientMetadata>()?.ProviderName;

    static string? Error(IConfiguration configuration)
    {
        try { ProviderSwitch.Create(configuration); return null; }
        catch (InvalidOperationException ex) { return ex.Message; }
    }

    static IConfigurationRoot Memory(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(Pairs(pairs)).Build();

    static IEnumerable<KeyValuePair<string, string?>> Pairs((string Key, string Value)[] pairs) =>
        pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value));

    static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}

/// <summary>
/// A scripted IChatClient: it echoes the prompt and reports fixed token counts.
/// The next chapter explains fakes; here it only proves the chat turn.
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"fake answer to: {messages.Last().Text}"))
        {
            Usage = new UsageDetails { InputTokenCount = 3, OutputTokenCount = 5 },
        });

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
