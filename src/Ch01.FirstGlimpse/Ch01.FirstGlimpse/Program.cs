// ============================================================================
// Chapter 1 — Your First Glimpse of an Agent
// You are NOT expected to understand every line yet. This is a teaser.
// Everything here is explained in full later: the host and DI in Chapter 4,
// ChatClientAgent and sessions in Chapter 5. Run it once to see the shape of
// what you'll be building throughout the book.
// ============================================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

// 1. Wire up an LLM chat client. The provider is chosen in appsettings.json /
//    User Secrets and works across Azure OpenAI, OpenAI, or
//    Ollama without changing this code — all set up in Chapter 4.
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

Console.WriteLine("=== Chapter 1: Your First Glimpse of an Agent ===\n");

// 2. Create an agent. The instructions are its persona, applied to every call.
ChatClientAgent agent = new(
    chatClient,
    instructions: "You are a friendly assistant for .NET developers. Be concise.",
    name: "FirstGlimpse");

// 3. Give it a session to remember the conversation, then ask one question.
AgentSession session = await agent.CreateSessionAsync();

AgentResponse response = await agent.RunAsync(
    "In one sentence, what is an AI agent?",
    session);

Console.WriteLine($"{agent.Name}: {response.Text}");
