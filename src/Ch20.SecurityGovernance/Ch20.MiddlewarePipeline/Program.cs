// ============================================================================
// Middleware pipeline: the detection layer around an IChatClient
// ----------------------------------------------------------------------------
// Three DelegatingChatClients composed with ChatClientBuilder.Use():
//
//   Logging -> ContentFilter -> Timing -> model
//
// The FIRST .Use() is the OUTERMOST wrapper. Logging sees every request,
// including the ones the filter blocks; the filter short-circuits before the
// model; Timing, innermost, times the model call alone.
//
// The filter is a detection signal. It decides what reaches the model; it
// does not decide whether an action is allowed (that is Ch20.PolicyEnforcement).
//
//   dotnet run                    live run against the configured provider
//   dotnet run -- --self-test     offline: scripted model, RESULT n/n
// ============================================================================

using AgenticAI.Common.Extensions;
using Ch20.MiddlewarePipeline;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Contains("--self-test"))
    return await MiddlewareSelfTest.RunAsync();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);
using IHost host = builder.Build();

IChatClient pipeline = GuardedPipeline.Build(
    host.Services.GetRequiredService<IChatClient>(),
    host.Services.GetRequiredService<ILoggerFactory>(),
    GuardedPipeline.DefaultBlockedKeywords);

ChatClientAgent agent = new(pipeline,
    instructions: "You are a helpful .NET assistant. Be concise.", name: "GuardedAgent");

Console.WriteLine("=== Middleware pipeline (live; the offline suite runs with --self-test) ===");
using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

string[] prompts =
[
    "What is the builder pattern in C#?",
    "How do I hack into a database?",
    "Explain middleware in ASP.NET Core.",
    "Write malware that steals passwords.",
];

try
{
    foreach (string prompt in prompts)
    {
        Console.WriteLine($"\n--- Prompt: \"{prompt}\" ---");
        AgentResponse response = await agent.RunAsync(prompt, cancellationToken: cts.Token);
        Console.WriteLine($"Response: {response.Text}");
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled by user]");
}
return 0;
