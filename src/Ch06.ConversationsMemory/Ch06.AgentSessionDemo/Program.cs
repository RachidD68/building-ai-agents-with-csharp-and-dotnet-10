// ============================================================================
// Chapter 6 — Conversations and Memory (Agent Session Demo)
// Fixes the amnesia problem by using InMemoryChatHistoryProvider.
// The agent now remembers everything within a conversation session.
//
// Key concepts:
//   - AgentSession:                 holds conversation state
//   - InMemoryChatHistoryProvider:  stores and replays chat history per session
//   - ChatClientAgentOptions:       wires the history provider into the agent
// ============================================================================

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

Console.WriteLine("=== Chapter 6: Agent Session with Memory ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ---------------------------------------------------------------
// Step 1: Create an agent WITH InMemoryChatHistoryProvider
// ---------------------------------------------------------------
// InMemoryChatHistoryProvider automatically:
//   - Stores user messages and agent responses in memory
//   - Replays the full history on each subsequent call
//   - Scopes history to the AgentSession instance
InMemoryChatHistoryProvider historyProvider = new(new InMemoryChatHistoryProviderOptions());

ChatClientAgentOptions agentOptions = new()
{
    Name = "MemoryBot",
    Description = "An assistant that remembers your conversation",
    ChatHistoryProvider = historyProvider,
    ChatOptions = new ChatOptions
    {
        Temperature = 0.7f
    }
};

ChatClientAgent agent = new(chatClient, options: agentOptions);

// Create a single session — all turns will share this session's history
AgentSession session = await agent.CreateSessionAsync(cts.Token);

Console.WriteLine($"Agent: {agent.Name}");
Console.WriteLine("Session created. The agent will now remember the conversation.\n");

try
{
    // ---------------------------------------------------------------
    // Turn 1: Introduce ourselves
    // ---------------------------------------------------------------
    string message1 = "My name is Rachid. I live in Seattle and I love C# programming.";
    Console.WriteLine($"You: {message1}");

    AgentResponse response1 = await agent.RunAsync(
        message1,
        session,
        cancellationToken: cts.Token);

    Console.WriteLine($"Agent: {response1.Text}\n");

    // ---------------------------------------------------------------
    // Turn 2: Ask the agent to recall — it should remember!
    // ---------------------------------------------------------------
    string message2 = "What's my name? Where do I live? What programming language do I love?";
    Console.WriteLine($"You: {message2}");

    AgentResponse response2 = await agent.RunAsync(
        message2,
        session,
        cancellationToken: cts.Token);

    Console.WriteLine($"Agent: {response2.Text}\n");

    // ---------------------------------------------------------------
    // Turn 3: Build on the conversation
    // ---------------------------------------------------------------
    string message3 = "Based on what you know about me, suggest a weekend project.";
    Console.WriteLine($"You: {message3}");

    AgentResponse response3 = await agent.RunAsync(
        message3,
        session,
        cancellationToken: cts.Token);

    Console.WriteLine($"Agent: {response3.Text}\n");

    // ---------------------------------------------------------------
    // Inspect the history stored in the session
    // ---------------------------------------------------------------
    Console.WriteLine(new string('=', 60));
    Console.WriteLine("SESSION HISTORY INSPECTION");
    Console.WriteLine(new string('=', 60));

    // GetMessages is the GA accessor on InMemoryChatHistoryProvider. It
    // returns the messages stored for this session — the same list the
    // provider replays to the model on each turn.
    List<ChatMessage> messages = historyProvider.GetMessages(session);
    Console.WriteLine($"Total messages in history: {messages.Count}\n");
    foreach (ChatMessage msg in messages)
    {
        string role = msg.Role.Value;
        string preview = (msg.Text ?? string.Empty).Length > 80
            ? string.Concat(msg.Text.AsSpan(0, 80), "...")
            : msg.Text ?? "(empty)";
        Console.WriteLine($"  [{role,-10}] {preview}");
    }

    // ---------------------------------------------------------------
    // Demonstrate session isolation: a second session has no memory
    // ---------------------------------------------------------------
    Console.WriteLine($"\n{new string('=', 60)}");
    Console.WriteLine("SESSION ISOLATION");
    Console.WriteLine(new string('=', 60));

    AgentSession session2 = await agent.CreateSessionAsync(cts.Token);
    AgentResponse isolatedResponse = await agent.RunAsync(
        "What is my name?",
        session2,
        cancellationToken: cts.Token);

    Console.WriteLine("New session — asking 'What is my name?'");
    Console.WriteLine($"Agent: {isolatedResponse.Text}");
    Console.WriteLine("\nAs expected, the new session has no memory of the first conversation.");
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled by user]");
}

Console.WriteLine("\n=== Agent Session Demo complete! ===");
