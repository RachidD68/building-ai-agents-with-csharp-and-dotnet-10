// ============================================================================
// Background agents: delegating work and collecting it later
// ----------------------------------------------------------------------------
// BackgroundAgentsProvider is the asynchronous form of agent composition. You
// hand the harness named agents; it sends the model six tools:
//
//   background_agents_start_task                 start a named agent, get a task id
//   background_agents_wait_for_first_completion  block, at most WaitTimeout
//   background_agents_get_task_results           collect finished output
//   background_agents_get_all_tasks              see what is outstanding
//   background_agents_continue_task              send more input to a task
//   background_agents_clear_completed_task       drop a finished task
//
// A task outlives the turn that started it. BackgroundAgentsProvider.
// ReleaseSessionAsync cancels and releases whatever is still running for a
// session; get the provider with agent.GetService<BackgroundAgentsProvider>()
// and release in a finally block. All of this is [Experimental("MAAI001")].
//
//   dotnet run                  live run against the configured provider
//   dotnet run -- --self-test   offline checks with scripted and hanging clients
// ============================================================================

using AgenticAI.Common.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Contains("--self-test")) return await SelfTest.RunAsync();

HostApplicationBuilder hostBuilder = Host.CreateApplicationBuilder(args);
hostBuilder.Services.AddLlmChatClient(hostBuilder.Configuration,
    attemptTimeout: TimeSpan.FromMinutes(8));

using IHost host = hostBuilder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Background agents ===");
Console.WriteLine();

// A background agent is an ordinary AIAgent the harness may run out of band.
ChatClientAgent researcher = new(
    chatClient,
    name: "Researcher",
    instructions: "You gather short factual notes on a product. Three bullet points, no more.");

ChatClientAgent copywriter = new(
    chatClient,
    name: "Copywriter",
    instructions: "You turn notes into one plain sentence a customer would understand.");

HarnessAgentOptions options = new()
{
    Name = "Coordinator",
    HarnessInstructions =
        "You coordinate work. When a request needs research or copywriting, start a " +
        "background task for the right specialist, then collect the result and " +
        "summarise it yourself. Do not do the specialists' work inline.",
    ChatOptions = new ChatOptions { Instructions = "Be concise." },
    BackgroundAgents = [researcher, copywriter],
    BackgroundAgentsProviderOptions = new BackgroundAgentsProviderOptions
    {
        WaitTimeout = TimeSpan.FromMinutes(3),   // the longest one wait call may block
    },
    DisableWebSearch = true,
    DisableFileMemory = true,
    DisableAgentSkillsProvider = true,
};

HarnessAgent coordinator = chatClient.AsHarnessAgent(options);
BackgroundAgentsProvider provider = coordinator.GetService<BackgroundAgentsProvider>()
    ?? throw new InvalidOperationException("The harness did not create a BackgroundAgentsProvider.");
AgentSession session = await coordinator.CreateSessionAsync(cts.Token);

const string task =
    "Produce one customer-facing sentence about the Contoso Ergonomic Keyboard. " +
    "Use the Researcher for the facts and the Copywriter for the wording.";

try
{
    Console.WriteLine($"You: {task}");
    Console.Write("Agent: ");
    await foreach (AgentResponseUpdate update in
        coordinator.RunStreamingAsync(task, session, cancellationToken: cts.Token))
    {
        Console.Write(update.Text);
    }
    Console.WriteLine();
    Console.WriteLine();
}
finally
{
    // A task nobody collects is still calling the model. Release on every path.
    IReadOnlyList<BackgroundTaskInfo> outstanding = provider.GetIncompleteTasks(session);
    Console.WriteLine($"Outstanding background tasks at the end of the turn: {outstanding.Count}");
    foreach (BackgroundTaskInfo info in outstanding)
    {
        Console.WriteLine($"  #{info.Id} {info.AgentName}: {info.Status} ({info.Description})");
    }
    await provider.ReleaseSessionAsync(session, cancelRunning: true, timeout: TimeSpan.FromSeconds(30), CancellationToken.None);
    Console.WriteLine($"Released. Incomplete tasks now: {provider.GetIncompleteTasks(session).Count}");
}

Console.WriteLine();
Console.WriteLine("=== Harness background-agents demo complete! ===");
return 0;
