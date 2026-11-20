// ============================================================================
// Safety context, per run and mid-run
// ----------------------------------------------------------------------------
// Two hooks, two moments:
//
//   AIContextProvider (SafetyContextProvider)
//     ProvideAIContextAsync runs once at the start of each agent run, with that
//     run's request messages. It adds instructions for THIS request. It keeps
//     no "current query" field, so concurrent requests cannot see each other's.
//
//   MessageInjectingChatClient (ChatClientAgentOptions.EnableMessageInjection)
//     Sits between FunctionInvokingChatClient and the model. A tool delegate
//     can enqueue a message for the session; it is sent with the next model
//     call of the same run. Here the document tool adds a safety note when the
//     text it retrieved contains instruction-like content.
//
//   dotnet run                    live run against the configured provider
//   dotnet run -- --self-test     offline: scripted model, real agent loop, RESULT n/n
// ============================================================================

using AgenticAI.Common.Extensions;
using Ch20.MessageInjector;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Contains("--self-test"))
    return await InjectorSelfTest.RunAsync();

HostApplicationBuilder hostBuilder = Host.CreateApplicationBuilder(args);
hostBuilder.Services.AddLlmChatClient(hostBuilder.Configuration);
using IHost host = hostBuilder.Build();

ChatClientAgent agent = SafetyAgent.Create(host.Services.GetRequiredService<IChatClient>(), new SafetyContextProvider());

Console.WriteLine("=== Safety context (live; the offline suite runs with --self-test) ===\n");
using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

string[] prompts =
[
    "What does the returns policy say? Look it up.",
    "What does the travel policy say? Look it up.",
    "Please reimburse employee E-2002 45 EUR for lunch.",
];
foreach (string prompt in prompts)
{
    AgentSession session = await agent.CreateSessionAsync(cts.Token);
    Console.WriteLine($">> {prompt}");
    AgentResponse response = await agent.RunAsync(prompt, session, cancellationToken: cts.Token);
    Console.WriteLine($"<< {response.Text}\n");
}
return 0;
