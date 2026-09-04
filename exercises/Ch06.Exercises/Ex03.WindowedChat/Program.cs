// ============================================================================
// Exercise 03 — Windowed Chat (Reducer)
// Wire a BUILT-IN MessageCountingChatReducer into the history provider, run a
// 15-turn conversation, and watch the input-token count stop growing once the
// window fills. The stored history still holds every message — reduction only
// trims what the model sees. FULLY STANDALONE.
//
// MessageCountingChatReducer is [Experimental("MEAI001")] — opted into via the
// .csproj <NoWarn>.
// ============================================================================

using System.ClientModel;
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

Console.WriteLine("=== Exercise: Windowed Chat (MessageCountingChatReducer) ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

const int windowSize = 6;

// The reducer keeps the system message + the most recent N non-system messages,
// trimming everything older BEFORE the request is sent to the model.
InMemoryChatHistoryProvider historyProvider = new(new InMemoryChatHistoryProviderOptions
{
    ChatReducer = new MessageCountingChatReducer(targetCount: windowSize)
});

ChatClientAgent agent = new(chatClient, new ChatClientAgentOptions
{
    Name = "WindowedAssistant",
    ChatHistoryProvider = historyProvider,
    ChatOptions = new ChatOptions { Instructions = "You are a helpful assistant. Be concise." }
});

AgentSession session = await agent.CreateSessionAsync(cts.Token);

Console.WriteLine($"Window size: {windowSize} messages. Running 15 turns.\n");
Console.WriteLine($"{"Turn",-5}{"InputTokens",-14}{"StoredMessages",-16}");
Console.WriteLine(new string('-', 35));

try
{
    for (int i = 1; i <= 15; i++)
    {
        string message = $"This is turn {i}. Briefly acknowledge with the turn number.";
        AgentResponse response = await agent.RunAsync(message, session, cancellationToken: cts.Token);

        long inputTokens = response.Usage?.InputTokenCount ?? 0;
        int storedMessages = historyProvider.GetMessages(session).Count;

        Console.WriteLine($"{i,-5}{inputTokens,-14}{storedMessages,-16}");
    }

    Console.WriteLine();
    Console.WriteLine("Observe: with the default BeforeMessagesRetrieval trigger the provider");
    Console.WriteLine("keeps the reduced view, so both input tokens AND stored messages");
    Console.WriteLine("plateau once the window fills — the running history converges to the");
    Console.WriteLine("window instead of growing without bound.");
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled]");
}

Console.WriteLine("\n=== Exercise complete! ===");

// ---------------------------------------------------------------
// Helper: build an IChatClient from configuration.
// ---------------------------------------------------------------
static IChatClient CreateChatClient(IConfiguration configuration)
{
    string provider = configuration["LlmProvider:Provider"]?.ToLowerInvariant() ?? "ollama";

    return provider switch
    {
        "azure" => new AzureOpenAIClient(
                new Uri(configuration["LlmProvider:AzureOpenAI:Endpoint"] ?? throw new InvalidOperationException("Missing endpoint")),
                new DefaultAzureCredential())
            .GetChatClient(configuration["LlmProvider:AzureOpenAI:Model"] ?? "gpt-5.6-luna")
            .AsIChatClient(),

        "openai" => new OpenAI.OpenAIClient(
                configuration["LlmProvider:OpenAI:ApiKey"] ?? throw new InvalidOperationException("Missing key"))
            .GetChatClient(configuration["LlmProvider:OpenAI:Model"] ?? "gpt-5.6-luna")
            .AsIChatClient(),

        "ollama" => new OllamaSharp.OllamaApiClient(
            new Uri(configuration["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"),
            configuration["LlmProvider:Ollama:Model"] ?? "llama3.2"),

        _ => throw new InvalidOperationException($"Unknown provider: '{provider}'")
    };
}
