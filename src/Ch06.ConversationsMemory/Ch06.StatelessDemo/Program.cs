// ============================================================================
// Chapter 6 — Conversations and Memory (Stateless Demo)
// Demonstrates the "amnesia problem": without session history, each call to
// the agent is independent. The agent forgets everything between calls.
//
// We send "My name is Rachid" and then ask "What's my name?" — the agent
// cannot answer because it has no memory of the first exchange.
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

Console.WriteLine("=== Chapter 6: The Amnesia Problem (Stateless Demo) ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Create an agent WITHOUT a chat history provider.
// Each call is completely independent — no memory between turns.
ChatClientAgent agent = new(
    chatClient,
    instructions: "You are a friendly assistant. Be concise.",
    name: "ForgetfulBot");

try
{
    // ---------------------------------------------------------------
    // Turn 1: Tell the agent our name
    // ---------------------------------------------------------------
    // IMPORTANT: the amnesia here comes from creating a NEW session for
    // each call. A session carries conversation history by default — reuse
    // one session and the agent remembers (that is Ch06.AgentSessionDemo's
    // subject). Throw the session away between turns and every call starts
    // from a blank slate.
    AgentSession session1 = await agent.CreateSessionAsync(cts.Token);

    string message1 = "My name is Rachid. I live in Seattle and I love C# programming.";
    Console.WriteLine($"You: {message1}");

    AgentResponse response1 = await agent.RunAsync(
        message1,
        session1,
        cancellationToken: cts.Token);

    Console.WriteLine($"Agent: {response1.Text}\n");

    // ---------------------------------------------------------------
    // Turn 2: Ask the agent what our name is (new session = amnesia!)
    // ---------------------------------------------------------------
    AgentSession session2 = await agent.CreateSessionAsync(cts.Token);

    string message2 = "What's my name? Where do I live? What programming language do I love?";
    Console.WriteLine($"You: {message2}");

    AgentResponse response2 = await agent.RunAsync(
        message2,
        session2,
        cancellationToken: cts.Token);

    Console.WriteLine($"Agent: {response2.Text}\n");

    // ---------------------------------------------------------------
    // Explain what happened
    // ---------------------------------------------------------------
    Console.WriteLine(new string('=', 60));
    Console.WriteLine("WHAT HAPPENED?");
    Console.WriteLine(new string('=', 60));
    Console.WriteLine("""
        The agent could not recall your name, location, or favourite language
        because each call used a separate session with no history provider.

        LLMs are inherently stateless — they don't remember previous requests.
        Every call is a fresh conversation unless you explicitly provide context.

        In the next demo (Ch06.AgentSessionDemo), we fix this using
        InMemoryChatHistoryProvider to maintain conversation history across turns.
        """);
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled by user]");
}

Console.WriteLine("\n=== Stateless Demo complete! ===");
