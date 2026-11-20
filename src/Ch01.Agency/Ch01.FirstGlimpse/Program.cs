// ============================================================================
// Your First Glimpse of an Agent
// You are NOT expected to understand every line yet. This is a teaser.
// Everything here is explained in full later: the host and DI in the development-environment chapter,
// ChatClientAgent and sessions in the first-agent chapter. Run it once to see the shape of
// what you'll be building throughout the book.
//
//   dotnet run                    against the configured model (Ollama llama3.2)
//   dotnet run -- --offline       no model: a canned client stands in, labelled as such
//   dotnet run -- --self-test     offline checks of the agent's shape; RESULT n/n
// ============================================================================

using Ch01.FirstGlimpse;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

if (args.Contains("--self-test")) return await SelfTest.RunAsync();

// Wire up an LLM chat client. The provider is chosen in appsettings.json / User Secrets:
// openai or ollama; Azure OpenAI through AgenticAI.Common.Azure (see the development-environment chapter).
// --offline registers no provider at all, so it needs no model settings.
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
bool offline = args.Contains("--offline");
if (!offline) builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = offline
    ? new OfflineChatClient()
    : host.Services.GetRequiredService<IChatClient>();

Console.WriteLine("=== Your First Glimpse of an Agent ===\n");

// Create an agent. The instructions are its persona, applied to every call.
ChatClientAgent agent = new(
    chatClient,
    instructions: "You are a friendly assistant for .NET developers. Be concise.",
    name: "FirstGlimpse");

// Give it a session to remember the conversation, then ask one question.
AgentSession session = await agent.CreateSessionAsync();

AgentResponse response = await agent.RunAsync(
    "In one sentence, what is an AI agent?",
    session);

Console.WriteLine($"{agent.Name}: {response.Text}");
return 0;
