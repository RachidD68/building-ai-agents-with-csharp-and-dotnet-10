// ============================================================================
// Exercise 04 — Where Does It Live? (Service-managed state)
// Two agents over the SAME chat client: one with an InMemoryChatHistoryProvider,
// one without. Run the same two turns on each, serialize both sessions, and
// compare StateBag.Count and payload size.
//
// The honest lesson: against a Chat Completions backend (Ollama)
// the wire protocol is stateless, so the framework keeps history client-side
// EITHER WAY — both payloads carry the transcript. ChatOptions.ConversationId
// only moves state server-side with a Responses/Foundry agent. FULLY STANDALONE.
// ============================================================================

using System.ClientModel;
using System.Text.Json;
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

Console.WriteLine("=== Exercise: Where Does It Live? ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Session serialization needs the framework's source-generated resolver.
// A bare new JsonSerializerOptions() throws NotSupportedException on Serialize.
JsonSerializerOptions jsonOptions =
    new(AgentAbstractionsJsonUtilities.DefaultOptions) { WriteIndented = true };

const string turn1 = "My name is Rachid and I am building an agent in C#.";
const string turn2 = "What is my name and what am I building?";

try
{
    // With an explicit in-memory history provider.
    await ProbeAsync("WITH InMemoryChatHistoryProvider",
        new ChatClientAgentOptions
        {
            Name = "WithProvider",
            ChatHistoryProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions()),
            ChatOptions = new ChatOptions { Instructions = "You are a helpful assistant. Be concise." }
        });

    // Without one (relies on the agent's default in-memory behavior).
    await ProbeAsync("WITHOUT an explicit provider",
        new ChatClientAgentOptions
        {
            Name = "WithoutProvider",
            ChatOptions = new ChatOptions { Instructions = "You are a helpful assistant. Be concise." }
        });

    Console.WriteLine("Why both carry the transcript: a Chat Completions backend is stateless,");
    Console.WriteLine("so the framework keeps history client-side regardless. With a");
    Console.WriteLine("Responses/Foundry agent, ChatOptions.ConversationId would point at a");
    Console.WriteLine("server-side conversation and the session would shrink to just that id.");
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled]");
}

Console.WriteLine("\n=== Exercise complete! ===");

// ---------------------------------------------------------------
// Run two turns against an agent and report what its session holds.
// ---------------------------------------------------------------
async Task ProbeAsync(string label, ChatClientAgentOptions options)
{
    ChatClientAgent agent = new(chatClient, options);
    AgentSession session = await agent.CreateSessionAsync(cts.Token);

    await agent.RunAsync(turn1, session, cancellationToken: cts.Token);
    AgentResponse answer = await agent.RunAsync(turn2, session, cancellationToken: cts.Token);

    JsonElement state = await agent.SerializeSessionAsync(session, jsonOptions, cts.Token);
    string json = JsonSerializer.Serialize(state, jsonOptions);
    bool transcriptInPayload = json.Contains("Rachid", StringComparison.OrdinalIgnoreCase);

    Console.WriteLine($"--- {label} ---");
    Console.WriteLine($"  Turn 2 answer:        {answer.Text}");
    Console.WriteLine($"  StateBag entries:     {session.StateBag.Count}");
    Console.WriteLine($"  Serialized size:      {json.Length:N0} chars");
    Console.WriteLine($"  Transcript in payload: {transcriptInPayload}\n");
}

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
