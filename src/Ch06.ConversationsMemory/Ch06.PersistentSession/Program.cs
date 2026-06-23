// ============================================================================
// Chapter 6 — Conversations and Memory (Persistent Session)
// Serialize an agent session to a JSON file so conversations survive app
// restarts. This simulates the "remember me" feature users expect.
//
// Flow:
//   1. Check if a saved session file exists
//   2. If yes, deserialize and continue the conversation
//   3. If no, start a new conversation
//   4. After each interaction, serialize the session to disk
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

Console.WriteLine("=== Chapter 6: Persistent Session ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ---------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------
string sessionFilePath = Path.Combine(AppContext.BaseDirectory,"conversation-session.json");

// Session serialization is source-generated: it needs the framework's
// JsonTypeInfo resolver. Copy AgentAbstractionsJsonUtilities.DefaultOptions
// and layer WriteIndented on top. A bare `new JsonSerializerOptions()` has no
// resolver and throws NotSupportedException at SerializeSessionAsync time.
JsonSerializerOptions jsonOptions =
    new(AgentAbstractionsJsonUtilities.DefaultOptions)
    {
        WriteIndented = true
    };

// ---------------------------------------------------------------
// Create the agent with InMemoryChatHistoryProvider
// ---------------------------------------------------------------
InMemoryChatHistoryProvider historyProvider = new(new InMemoryChatHistoryProviderOptions());

ChatClientAgentOptions agentOptions = new()
{
    Name = "PersistentBot",
    Description = "An assistant with persistent memory across app restarts",
    ChatHistoryProvider = historyProvider
};

ChatClientAgent agent = new(chatClient, options: agentOptions);

// ---------------------------------------------------------------
// Step 1: Load or create session
// ---------------------------------------------------------------
AgentSession session;
bool isResumed = false;
if (File.Exists(sessionFilePath))
{
    Console.WriteLine($"Found saved session at: {sessionFilePath}");
    Console.WriteLine("Restoring previous conversation...\n");

    string savedJson = await File.ReadAllTextAsync(sessionFilePath, cts.Token);
    JsonElement serializedState = JsonSerializer.Deserialize<JsonElement>(savedJson);

    session = await agent.DeserializeSessionAsync(serializedState,jsonOptions,cts.Token);

    isResumed = true;

    // Show what we restored. GetMessages is the GA accessor on the provider;
    // it returns the messages the provider rehydrated into this session.
    List<ChatMessage> history = historyProvider.GetMessages(session);
    Console.WriteLine($"Restored {history.Count} messages from previous session.\n");
}
else
{
    Console.WriteLine("No saved session found. Starting a new conversation.\n");
    session = await agent.CreateSessionAsync(cts.Token);
}

try
{
    // ---------------------------------------------------------------
    // Step 2: Run the interactive conversation loop
    // ---------------------------------------------------------------
    if (!isResumed)
    {
        // First-run: seed the conversation so the reader can see
        // persistence at work when they run the app again.
        string[] seedMessages =
        [
            "My name is Rachid. I'm writing a book about building AI agents with C# and .NET.",
            "My favourite topics are agent architectures and the Microsoft Agent Framework."
        ];

        foreach (string msg in seedMessages)
        {
            Console.WriteLine($"You: {msg}");
            AgentResponse response = await agent.RunAsync(msg, session, cancellationToken: cts.Token);
            Console.WriteLine($"Agent: {response.Text}\n");
        }
    }
    else
    {
        // Resumed session: prove the agent remembers by asking about
        // information from the previous run.
        string recallMessage = "Can you remind me what my name is and what I'm working on?";
        Console.WriteLine($"You (after restart): {recallMessage}");

        AgentResponse recallResponse = await agent.RunAsync(recallMessage, session, cancellationToken: cts.Token);
        Console.WriteLine($"Agent: {recallResponse.Text}\n");
    }

    // Allow the user to type additional messages
    Console.WriteLine("Type a message (or 'quit' to exit):\n");

    while (!cts.Token.IsCancellationRequested)
    {
        Console.Write("You: ");
        string? input = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(input) ||
            input.Equals("quit", StringComparison.OrdinalIgnoreCase))
        {
            break;
        }

        AgentResponse response = await agent.RunAsync( input, session, cancellationToken: cts.Token);
        Console.WriteLine($"Agent: {response.Text}\n");
    }

    // ---------------------------------------------------------------
    // Step 3: Serialize the session to disk
    // ---------------------------------------------------------------
    Console.WriteLine("\nSaving session to disk...");

    JsonElement serializedSession = await agent.SerializeSessionAsync(session,jsonOptions,cts.Token);

    string json = JsonSerializer.Serialize(serializedSession, jsonOptions);
    await File.WriteAllTextAsync(sessionFilePath, json, cts.Token);

    Console.WriteLine($"Session saved to: {sessionFilePath}");
    Console.WriteLine($"File size: {new FileInfo(sessionFilePath).Length:N0} bytes");
    Console.WriteLine("\nRun this app again to continue the conversation!");
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled by user]");
}

Console.WriteLine("\n=== Persistent Session demo complete! ===");
