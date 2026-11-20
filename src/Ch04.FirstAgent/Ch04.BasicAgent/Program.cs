// ============================================================================
// Contoso expense help: a first agent
// ----------------------------------------------------------------------------
// A ChatClientAgent that answers Contoso employees' expense-policy questions.
// No tools, no effects. The program shows the three ways to build the agent,
// a run with no session, a run with one, and options overridden for one call.
//
//   dotnet run    against the configured model (Ollama llama3.2 by default)
//
// The offline proof of this agent is Ch04.AgentTests: a fake IChatClient
// checks what reaches the model without calling one.
// ============================================================================

using System.ClientModel;
using Ch04.FirstAgent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);
using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

// Ctrl+C cancels the token, and the token reaches the provider call, so an
// abandoned answer stops generating instead of running on in the background.
using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Contoso expense help: a first agent ===\n");

// 1. Constructor with instructions: the pattern every Contoso host uses.
ChatClientAgent agent = ContosoPolicy.Create(chatClient);

// 2. An options object: chat defaults pinned when the agent is built.
ChatClientAgent pinned = new(chatClient, new ChatClientAgentOptions
{
    Name = "ContosoExpenseHelpPinned",
    ChatOptions = new ChatOptions
    {
        Instructions = ContosoPolicy.Instructions,
        Temperature = 0.2f,
        MaxOutputTokens = 200,
    },
});

// 3. The AsAIAgent extension on any IChatClient.
AIAgent quick = chatClient.AsAIAgent(instructions: ContosoPolicy.Instructions, name: "ContosoQuick");

try
{
    // No session: the run starts with no history and keeps none.
    await Ask(agent, "Is a 42 EUR taxi receipt approved automatically?");

    // A session carries the conversation, so the second question can say "it".
    AgentSession session = await agent.CreateSessionAsync(cts.Token);
    await Ask(agent, "My hotel claim is 180 EUR.", session);
    await Ask(agent, "Who has to approve it?", session);

    // Options for one call only: the agent's own defaults stay as they were.
    ChatClientAgentRunOptions shortAnswer = new(new ChatOptions { MaxOutputTokens = 12 });
    await Ask(agent, "Explain the whole expense policy.", options: shortAnswer);

    await Ask(pinned, "Can I approve my own claim?");
    await Ask(quick, "Can you pay my claim now?");
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    Console.WriteLine("\n[cancelled]");
    return 130;
}
catch (HttpRequestException ex)                  // Ollama and other HTTP providers
{
    Console.WriteLine($"[model call failed] {ex.StatusCode?.ToString() ?? "no response"}: {ex.Message}");
    return 1;
}
catch (ClientResultException ex)                 // OpenAI-compatible SDKs: ex.Status is the HTTP status
{
    Console.WriteLine($"[model call failed] HTTP {ex.Status}: {ex.Message}");
    return 1;
}
return 0;

async Task Ask(AIAgent who, string question, AgentSession? session = null, AgentRunOptions? options = null)
{
    AgentResponse response = await who.RunAsync(question, session, options, cts.Token);
    Console.WriteLine($"You: {question}");
    Console.WriteLine($"{who.Name}: {response.Text.Trim()}");
    Console.WriteLine($"  [finish: {response.FinishReason?.Value ?? "none"}, " +
                      $"tokens in/out: {response.Usage?.InputTokenCount}/{response.Usage?.OutputTokenCount}]\n");
}
