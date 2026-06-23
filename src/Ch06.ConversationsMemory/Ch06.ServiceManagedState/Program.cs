// ============================================================================
// Chapter 6 — Conversations and Memory (Service-Managed State)
// Where does the conversation actually live?
//
// Two storage models, same two-turn conversation:
//
//   CLIENT-MANAGED  — InMemoryChatHistoryProvider keeps the messages in the
//                     AgentSession's StateBag. Serializing the session writes
//                     the full transcript. YOU own persistence.
//
//   SERVICE-MANAGED — no client history provider. The service (e.g. an
//                     OpenAI Responses or Microsoft Foundry agent) stores the
//                     transcript server-side and hands back a conversation id.
//                     The session holds (essentially) just that id; you resume
//                     by passing it via ChatOptions.ConversationId.
//
// This sample runs against the provider-agnostic AddLlmChatClient. With a
// Chat Completions provider (the default Ollama/llama3.2) the wire protocol
// is stateless, so the framework ALWAYS keeps history client-side. The
// service-managed section shows the ChatOptions.ConversationId lever — the
// thing a Responses/Foundry agent populates to move the transcript server-side.
// ============================================================================

using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

Console.WriteLine("=== Chapter 6: Where Does the Conversation Live? ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Session serialization needs the framework's source-generated type-info
// resolver. Copy AgentAbstractionsJsonUtilities.DefaultOptions and layer
// pretty-printing on top — a bare new JsonSerializerOptions throws at runtime.
JsonSerializerOptions jsonOptions =
    new(AgentAbstractionsJsonUtilities.DefaultOptions) { WriteIndented = true };

string turn1 = "My name is Rachid and I am writing a book about agents in C#.";
string turn2 = "What is my name and what am I writing about?";

try
{
    // ==================================================================
    // CLIENT-MANAGED: history lives in the session's StateBag.
    // ==================================================================
    Console.WriteLine("--- Client-managed (InMemoryChatHistoryProvider) ---");

    ChatClientAgent clientManaged = new(chatClient, new ChatClientAgentOptions
    {
        Name = "ClientManagedBot",
        ChatHistoryProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions()),
        ChatOptions = new ChatOptions { Instructions = "You are a helpful assistant. Be concise." }
    });

    AgentSession clientSession = await clientManaged.CreateSessionAsync(cts.Token);
    await clientManaged.RunAsync(turn1, clientSession, cancellationToken: cts.Token);
    AgentResponse clientAnswer = await clientManaged.RunAsync(turn2, clientSession, cancellationToken: cts.Token);

    Console.WriteLine($"  Turn 2 answer: {clientAnswer.Text}");
    Console.WriteLine($"  StateBag entries after 2 turns: {clientSession.StateBag.Count}");

    JsonElement clientState = await clientManaged.SerializeSessionAsync(clientSession, jsonOptions, cts.Token);
    string clientJson = JsonSerializer.Serialize(clientState, jsonOptions);
    bool transcriptInPayload = clientJson.Contains("Rachid", StringComparison.OrdinalIgnoreCase);
    Console.WriteLine($"  Serialized session: {clientJson.Length:N0} chars; transcript embedded: {transcriptInPayload}");

    // ==================================================================
    // SERVICE-MANAGED: the conversation id is the lever.
    // A server-backed agent (OpenAI Responses, Microsoft Foundry) keeps the
    // transcript server-side and returns a ConversationId. You resume by
    // passing it back via ChatOptions.ConversationId instead of replaying
    // the messages. The session then holds little more than that id.
    // ==================================================================
    Console.WriteLine("\n--- Service-managed (the conversation-id lever) ---");

    ChatOptions resumeOptions = new()
    {
        Instructions = "You are a helpful assistant. Be concise.",
        ConversationId = "conv_demo_12345"   // a server-side conversation id
    };
    Console.WriteLine($"  ChatOptions.ConversationId set to: {resumeOptions.ConversationId}");
    Console.WriteLine("  A Responses/Foundry agent would round-trip this id and load the");
    Console.WriteLine("  transcript server-side — no messages travel in the request body.");

    // The id a server-backed agent assigns surfaces on the response.
    Console.WriteLine($"  Response.ResponseId (turn 1, client-managed run): {clientAnswer.ResponseId ?? "(none)"}");

    Console.WriteLine("\n--- The point ---");
    Console.WriteLine("  Against a Chat Completions provider (this run) the wire protocol is");
    Console.WriteLine("  stateless, so the framework ALWAYS keeps history client-side — the");
    Console.WriteLine("  transcript is in YOUR session payload and YOU persist and secure it.");
    Console.WriteLine("  Swap AddLlmChatClient to a Responses/Foundry agent and the same code");
    Console.WriteLine("  becomes service-managed: the transcript moves server-side and the");
    Console.WriteLine("  session shrinks to a conversation id. The agent contract is identical.");
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled by user]");
}

Console.WriteLine("\n=== Service-Managed State demo complete! ===");
