// ============================================================================
// Chapter 13 — Checkpointed Approval: durable human-in-the-loop
// ----------------------------------------------------------------------------
// Ch13.ApprovalWorkflow paused on a RequestPort and waited for a human decision
// in the same process. That is fine for a CLI, but a real approval can take
// hours or days — long enough for the host to restart. This version makes the
// pause durable.
//
// The workflow runs with a CheckpointManager. At the end of every superstep the
// runtime captures a checkpoint: every executor's state, the pending messages,
// and — crucially — the pending human request. We run until the workflow pauses
// for approval, grab that checkpoint, then throw the first run away to simulate
// a process restart. A *second* run resumes from the checkpoint, re-emits the
// same approval request, takes the decision, and finishes.
//
// A stateful RevisionTracker executor persists its revision counter across the
// checkpoint via OnCheckpointingAsync / OnCheckpointRestoredAsync, proving that
// executor state — not just framework plumbing — survives the restart.
//
// This sample uses an in-memory CheckpointManager so it runs as a single
// program. For a genuine cross-process restart, swap in a durable store:
//   var store = new FileSystemJsonCheckpointStore(new DirectoryInfo("./cp"));
//   CheckpointManager cp = CheckpointManager.CreateJson(store);
// (Dispose the store when done — it holds a lock on the directory.)
//
// Runs against Ollama (llama3.2) by default; see appsettings.json.
// ============================================================================

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

HostApplicationBuilder hostBuilder = Host.CreateApplicationBuilder(args);
hostBuilder.Services.AddLlmChatClient(hostBuilder.Configuration);

using IHost host = hostBuilder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Chapter 12: Checkpointed Approval (durable HITL) ===\n");

AIAgent drafter = new ChatClientAgent(
    chatClient,
    instructions: "You are a concise technical writer. Draft a short paragraph " +
                  "(3-4 sentences) on the topic you are given. Output only the paragraph.",
    name: "Drafter");

// One CheckpointManager, shared by both the first run and the resumed run.
CheckpointManager checkpoints = CheckpointManager.CreateInMemory();
string sessionId = Guid.NewGuid().ToString();
string topic = "the value of code review in software teams";

// The RequestPort instance is reused across both builds so its identity is
// stable; the workflow instances themselves are fresh per run.
RequestPort approvalPort = RequestPort.Create<string, string>("human-approval");

Workflow BuildApprovalWorkflow()
{
    ExecutorBinding draftNode = ExecutorBindingExtensions.BindAsExecutor<string, string>(
        async (input, context, ct) =>
        {
            AgentSession session = await drafter.CreateSessionAsync(ct);
            AgentResponse response = await drafter.RunAsync(input, session, cancellationToken: ct);
            return response.Text.Trim();
        },
        id: "draft");

    ExecutorBinding trackerNode = new RevisionTracker().BindExecutor();
    ExecutorBinding portNode = approvalPort.BindAsExecutor();
    ExecutorBinding finalNode = new Finalizer().BindExecutor();

    return new WorkflowBuilder(draftNode)
        .AddEdge(draftNode, trackerNode)
        .AddEdge(trackerNode, portNode)
        .AddEdge(portNode, finalNode)
        .WithOutputFrom(finalNode)
        .WithName("CheckpointedApproval")
        .Build();
}

// ---------------------------------------------------------------
// Run 1: execute until the workflow pauses for human approval,
// capture the checkpoint at that superstep, then STOP — as if the
// process crashed before anyone clicked "approve."
// ---------------------------------------------------------------
Console.WriteLine("--- Run 1: draft, then pause for approval (then 'crash') ---\n");

CheckpointInfo? pauseCheckpoint = null;

StreamingRun firstRun = await InProcessExecution.RunStreamingAsync(
    BuildApprovalWorkflow(), topic, checkpoints, sessionId, cts.Token);

await foreach (WorkflowEvent evt in firstRun.WatchStreamAsync(cts.Token))
{
    if (evt is SuperStepCompletedEvent step && step.CompletionInfo?.Checkpoint is { } checkpoint)
    {
        pauseCheckpoint = checkpoint;
    }

    if (evt is RequestInfoEvent request &&
        request.Request.TryGetDataAs<string>(out string? draft))
    {
        Console.WriteLine($"Draft awaiting approval:\n{draft}\n");
        Console.WriteLine($"Captured checkpoint: {pauseCheckpoint?.CheckpointId}");
        Console.WriteLine(">>> Simulating process crash before the human responds. <<<\n");
        break; // walk away without answering
    }
}

// ---------------------------------------------------------------
// Run 2: a brand-new workflow instance resumes from the checkpoint.
// The runtime replays state, re-emits the approval request, and we
// answer it this time. The RevisionTracker's counter is restored.
// ---------------------------------------------------------------
Console.WriteLine("--- Run 2: restart, resume from checkpoint, approve ---\n");

StreamingRun resumedRun = await InProcessExecution.ResumeStreamingAsync(
    BuildApprovalWorkflow(), pauseCheckpoint!, checkpoints, cts.Token);

await foreach (WorkflowEvent evt in resumedRun.WatchStreamAsync(cts.Token))
{
    switch (evt)
    {
        case RequestInfoEvent request
            when request.Request.TryGetDataAs<string>(out string? draft):
            Console.WriteLine("Approval request re-emitted after restart.");
            Console.WriteLine("Human approves the draft.\n");
            ExternalResponse response = request.Request.CreateResponse(draft ?? "");
            await resumedRun.SendResponseAsync(response);
            break;

        case WorkflowOutputEvent output:
            Console.WriteLine(output.Data);
            break;
    }
}

Console.WriteLine("\n=== Checkpointed approval demo complete! ===");

// ---------------------------------------------------------------
// A stateful executor whose state survives checkpoint/restore.
// ---------------------------------------------------------------

/// <summary>
/// Passes the draft through unchanged but tracks how many drafts it has seen.
/// The counter is written during checkpointing and read back on restore, so it
/// is correct even after the workflow resumes in a different process.
/// </summary>
internal sealed class RevisionTracker() : Executor<string, string>("revision-tracker")
{
    private const string StateKey = "revisionCount";
    private int _revisions;

    public override ValueTask<string> HandleAsync(
        string draft, IWorkflowContext context, CancellationToken ct)
    {
        _revisions++;
        Console.WriteLine($"  [RevisionTracker] revision count: {_revisions}");
        return ValueTask.FromResult(draft);
    }

    protected override async ValueTask OnCheckpointingAsync(
        IWorkflowContext context, CancellationToken ct = default)
    {
        await context.QueueStateUpdateAsync(StateKey, _revisions, cancellationToken: ct);
    }

    protected override async ValueTask OnCheckpointRestoredAsync(
        IWorkflowContext context, CancellationToken ct = default)
    {
        _revisions = await context.ReadStateAsync<int>(StateKey, cancellationToken: ct);
        Console.WriteLine($"  [RevisionTracker] restored revision count: {_revisions}");
    }
}

/// <summary>Formats the approved draft as the workflow's output.</summary>
[YieldsOutput(typeof(string))]
internal sealed class Finalizer() : Executor<string>("finalizer")
{
    public override async ValueTask HandleAsync(
        string approved, IWorkflowContext context, CancellationToken ct)
        => await context.YieldOutputAsync($"[APPROVED]\n{approved}", ct);
}
