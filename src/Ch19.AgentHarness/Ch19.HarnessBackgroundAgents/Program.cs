// ============================================================================
// Chapter 19 — Background agents: delegating work and collecting it later
// ----------------------------------------------------------------------------
// Chapter 12 composed agents synchronously: the orchestrator called a
// specialist and waited. BackgroundAgentsProvider is the asynchronous form.
// You hand the harness a set of named agents; it injects tools the model uses
// to start work, then carry on and collect results later:
//
//   background_agents_start_task     kick a named agent off, get a task id
//   wait_for_first_completion        block until one finishes
//   get_task_results                 collect finished output
//   get_all_tasks                    see what is outstanding
//   continue_task                    send more input to a running task
//   clear_completed_task             drop a finished task
//
// BackgroundAgentsProvider.ReleaseSessionAsync (added in 1.18) cancels and
// releases everything still running for a session -- important, because a
// task nobody collects is a task still burning tokens.
//
// All of this is [Experimental("MAAI001")] as of 1.22.
// ============================================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

HostApplicationBuilder hostBuilder = Host.CreateApplicationBuilder(args);
hostBuilder.Services.AddLlmChatClient(hostBuilder.Configuration,
    attemptTimeout: TimeSpan.FromMinutes(8));

using IHost host = hostBuilder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Chapter 19: Background agents ===");
Console.WriteLine();

// ── Two specialists, as plain agents ──────────────────────────────────
// Nothing special about them: a background agent is an ordinary AIAgent that
// the harness is allowed to run out of band.
ChatClientAgent researcher = new(
    chatClient,
    name: "Researcher",
    instructions: "You gather short factual notes on a product. Three bullet points, no more.");

ChatClientAgent copywriter = new(
    chatClient,
    name: "Copywriter",
    instructions: "You turn notes into one plain sentence a customer would understand.");

Console.WriteLine("Background agents available to the harness:");
Console.WriteLine("  Researcher  — gathers short factual notes");
Console.WriteLine("  Copywriter  — turns notes into a customer-facing sentence");
Console.WriteLine();

// ── The harness that delegates to them ────────────────────────────────
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
        // How long wait_for_first_completion will block before giving up.
        WaitTimeout = TimeSpan.FromMinutes(3),
    },

    DisableWebSearch = true,
    DisableFileMemory = true,
    DisableAgentSkillsProvider = true,
};

HarnessAgent coordinator = chatClient.AsHarnessAgent(options);
AgentSession session = await coordinator.CreateSessionAsync(cts.Token);

const string task =
    "Produce one customer-facing sentence about the Contoso Ergonomic Keyboard. " +
    "Use the Researcher for the facts and the Copywriter for the wording.";

Console.WriteLine($"You: {task}");
Console.Write("Agent: ");
await foreach (AgentResponseUpdate update in
    coordinator.RunStreamingAsync(task, session, cancellationToken: cts.Token))
{
    Console.Write(update.Text);
}
Console.WriteLine();
Console.WriteLine();

// ── Always release the session ────────────────────────────────────────
// Anything still running is cancelled here. Skipping this is how a demo
// leaves model calls burning after the console has already exited.
BackgroundAgentsProvider? provider = options.AIContextProviders?
    .OfType<BackgroundAgentsProvider>().FirstOrDefault();

IReadOnlyList<BackgroundTaskInfo>? outstanding = null;
try
{
    outstanding = provider?.GetIncompleteTasks(session);
}
catch (Exception)
{
    // The provider is owned by the harness in this configuration; if it is
    // not reachable from options, releasing is still the harness's job.
}

Console.WriteLine($"Outstanding background tasks at exit: {outstanding?.Count ?? 0}");
if (provider is not null)
{
    await provider.ReleaseSessionAsync(session, true, null, cts.Token);
    Console.WriteLine("Session released — any running background task was cancelled.");
}

Console.WriteLine();
Console.WriteLine("=== Harness background-agents demo complete! ===");
