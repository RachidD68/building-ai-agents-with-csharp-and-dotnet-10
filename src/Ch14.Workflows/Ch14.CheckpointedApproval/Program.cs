// ============================================================================
// Chapter 13 — Checkpointed Approval: durable human-in-the-loop
// ----------------------------------------------------------------------------
// Ch14.ApprovalWorkflow paused on a RequestPort and waited for a human decision
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
// Modes:
//   dotnet run                       in-memory demo: both runs in one process;
//                                    the approval is SIMULATED (a fixed decision)
//   dotnet run -- prepare            run to the approval point, persist the
//                                    checkpoints to disk, print the session id
//   dotnet run -- resume <session> <approve|reject>
//                                    a NEW process loads the checkpoint from
//                                    the same folder and completes the workflow
//   dotnet run -- --self-test        prepare and resume as two separate child
//                                    processes, plus fail-closed cases (offline)
// prepare and resume persist with FileSystemJsonCheckpointStore; --store <folder>
// picks the folder (default: a folder under %TEMP%). --offline swaps the model
// for a scripted IChatClient, so any mode runs without a provider. Every mode
// fails closed: an empty draft is never approved.
//
// Runs against Ollama (llama3.2) by default; see appsettings.json.
// ============================================================================

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

if (args.Contains("--self-test")) return CrossProcessSelfTest.Run();

// "prepare" / "resume" select a mode; anything else runs the original demo.
string[] positional = PositionalArgs();
string? mode = positional.FirstOrDefault() is "prepare" or "resume" ? positional[0] : null;
bool offline = args.Contains("--offline");

using IHost? host = offline ? null : BuildHost();
// --empty-draft (offline only) makes the scripted model return nothing, to
// show that an empty draft is never approved.
IChatClient chatClient = host?.Services.GetRequiredService<IChatClient>()
    ?? new ScriptedChatClient(args.Contains("--empty-draft") ? "" : ScriptedChatClient.Draft);

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

AIAgent drafter = new ChatClientAgent(
    chatClient,
    instructions: "You are a concise technical writer. Draft a short paragraph " +
                  "(3-4 sentences) on the topic you are given. Output only the paragraph.",
    name: "Drafter");

string topic = "the value of code review in software teams";

// The RequestPort instance is reused across both builds so its identity is
// stable; the workflow instances themselves are fresh per run.
RequestPort approvalPort = RequestPort.Create<string, string>("human-approval");

// The approval response is the approved draft text, except for this one word,
// which routes to the rejection handler instead of the finalizer.
const string RejectDecision = ApprovalDecisions.RejectReply;

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
    ExecutorBinding rejectNode = new Rejection().BindExecutor();

    return new WorkflowBuilder(draftNode)
        .AddEdge(draftNode, trackerNode)
        .AddEdge(trackerNode, portNode)
        // An empty reply matches neither edge, so it can never reach the finalizer.
        .AddEdge<string>(portNode, finalNode, reply => !string.IsNullOrWhiteSpace(reply) && reply != RejectDecision)
        .AddEdge<string>(portNode, rejectNode, reply => reply == RejectDecision)
        .WithOutputFrom(finalNode, rejectNode)
        .WithName("CheckpointedApproval")
        .Build();
}

string storePath = OptionValue("--store")
    ?? Path.Combine(Path.GetTempPath(), "AgenticAI", "Ch14.CheckpointedApproval");

switch (mode)
{
    case null:
        break; // the in-memory demo below

    case "prepare":
        return await PrepareAsync();

    case "resume" when positional.Length >= 3:
        return await ResumeAsync(positional[1], positional[2]);

    default:
        Console.Error.WriteLine(
            "Usage: [prepare | resume <session-id> <approve|reject>] [--store <folder>] [--offline] | --self-test");
        return 2;
}

Console.WriteLine("=== Chapter 13: Checkpointed Approval (durable HITL) ===\n");

// One CheckpointManager, shared by both the first run and the resumed run.
CheckpointManager checkpoints = CheckpointManager.CreateInMemory();
string sessionId = Guid.NewGuid().ToString();

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

if (pauseCheckpoint is null)
{
    Console.Error.WriteLine("DEMO FAILED: no checkpoint was captured at the approval point. Nothing was finalized.");
    return 1;
}

// ---------------------------------------------------------------
// Run 2: a brand-new workflow instance resumes from the checkpoint.
// The runtime replays state, re-emits the approval request, and we
// answer it this time. The RevisionTracker's counter is restored.
//
// Nobody is at the keyboard in this demo, so the decision is
// SIMULATED: a fixed, typed ApprovalDecision, printed as such. The
// prepare/resume modes take a real decision from the command line.
// ---------------------------------------------------------------
const ApprovalDecision SimulatedDecision = ApprovalDecision.Approve;

Console.WriteLine("--- Run 2: restart, resume from checkpoint, decide ---\n");

StreamingRun resumedRun = await InProcessExecution.ResumeStreamingAsync(
    BuildApprovalWorkflow(), pauseCheckpoint, checkpoints, cts.Token);

string? pendingReason = null;
await foreach (WorkflowEvent evt in resumedRun.WatchStreamAsync(cts.Token))
{
    switch (evt)
    {
        case RequestInfoEvent request
            when request.Request.TryGetDataAs<string>(out string? draft):
            Console.WriteLine("Approval request re-emitted after restart.");
            Console.WriteLine($"SIMULATED human decision: {SimulatedDecision.ToString().ToLowerInvariant()} " +
                              "(no reviewer in this demo; use prepare/resume to decide yourself).\n");

            // Fail closed: an approval needs a non-empty draft to approve.
            if (!ApprovalDecisions.TryCreateReply(SimulatedDecision, draft, out string? reply))
            {
                pendingReason = "the draft is empty, so there is nothing to approve.";
                break;
            }
            await resumedRun.SendResponseAsync(request.Request.CreateResponse(reply));
            break;

        case WorkflowOutputEvent output:
            Console.WriteLine(output.Data);
            break;
    }

    if (pendingReason is not null) break;   // leave the event loop, not just the switch
}

if (pendingReason is not null)
{
    Console.Error.WriteLine($"DEMO STOPPED: {pendingReason} Review left pending; nothing was finalized.");
    return 2;
}

Console.WriteLine("\n=== Checkpointed approval demo complete! ===");
return 0;

// ---------------------------------------------------------------
// prepare — process 1: run until the approval request, persist the
// checkpoints to a folder, print the session id, and exit.
// ---------------------------------------------------------------
async Task<int> PrepareAsync()
{
    Console.WriteLine("=== Chapter 13: Checkpointed Approval — prepare ===\n");

    Directory.CreateDirectory(storePath);
    using FileSystemJsonCheckpointStore store = new(new DirectoryInfo(storePath));
    CheckpointManager durable = CheckpointManager.CreateJson(store);
    string session = Guid.NewGuid().ToString("N");

    CheckpointInfo? latest = null;
    bool paused = false;

    await using (StreamingRun run = await InProcessExecution.RunStreamingAsync(
        BuildApprovalWorkflow(), topic, durable, session, cts.Token))
    {
        await foreach (WorkflowEvent evt in run.WatchStreamAsync(cts.Token))
        {
            if (evt is RequestInfoEvent request && request.Request.TryGetDataAs<string>(out string? draft))
            {
                Console.WriteLine($"Draft awaiting approval:\n{draft}\n");
                paused = true;
            }

            // The superstep that raised the request ends with a checkpoint that
            // holds the pending approval. Once it is on disk, walk away.
            if (evt is SuperStepCompletedEvent step && step.CompletionInfo?.Checkpoint is { } cp)
            {
                latest = cp;
                if (paused) break;
            }
        }
    }

    if (!paused || latest is null)
    {
        Console.Error.WriteLine("PREPARE FAILED: the workflow never reached the approval point.");
        return 1;
    }

    Console.WriteLine($"Store:      {storePath}");
    Console.WriteLine($"Session:    {session}");
    Console.WriteLine($"Checkpoint: {latest.CheckpointId}");
    Console.WriteLine($"\nResume in a new process:  dotnet run -- resume {session} approve");
    return 0;
}

// ---------------------------------------------------------------
// resume — process 2: load the latest checkpoint for the session
// from the same folder, answer the re-emitted request, complete.
// Anything wrong with the store or the checkpoint fails closed:
// nothing is finalized and the exit code is non-zero.
// ---------------------------------------------------------------
async Task<int> ResumeAsync(string session, string decision)
{
    Console.WriteLine("=== Chapter 13: Checkpointed Approval — resume ===\n");

    if (!ApprovalDecisions.TryParse(decision, out ApprovalDecision choice))
        return Fail($"unknown decision \"{decision}\" (use approve or reject).", 2);

    if (!Directory.Exists(storePath))
        return Fail($"no checkpoint store at {storePath}.");

    try
    {
        using FileSystemJsonCheckpointStore store = new(new DirectoryInfo(storePath));
        CheckpointManager durable = CheckpointManager.CreateJson(store);

        CheckpointInfo? latest = await durable.GetLatestCheckpointAsync(session, cts.Token);
        if (latest is null)
            return Fail($"no checkpoint for session {session} in {storePath}.");

        Console.WriteLine($"Resuming session {session} from checkpoint {latest.CheckpointId}\n");

        // The latest checkpoint of a session that was already decided has no
        // request left to replay, and watching it would wait forever. Give the
        // re-emitted request a bounded window; once it arrives, disarm the timer.
        using CancellationTokenSource watchdog = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        watchdog.CancelAfter(TimeSpan.FromSeconds(30));

        bool answered = false;
        bool emptyDraft = false;
        string? result = null;
        string? error = null;
        try
        {
            await using StreamingRun run = await InProcessExecution.ResumeStreamingAsync(
                BuildApprovalWorkflow(), latest, durable, cts.Token);

            await foreach (WorkflowEvent evt in run.WatchStreamAsync(watchdog.Token))
            {
                switch (evt)
                {
                    case RequestInfoEvent request when request.Request.TryGetDataAs<string>(out string? draft):
                        watchdog.CancelAfter(Timeout.InfiniteTimeSpan);
                        answered = true;
                        Console.WriteLine("Approval request re-emitted in a new process.");
                        Console.WriteLine($"Human decision: {choice.ToString().ToLowerInvariant()}\n");
                        if (!ApprovalDecisions.TryCreateReply(choice, draft, out string? reply))
                        {
                            emptyDraft = true;   // fail closed: never approve an empty draft
                            break;
                        }
                        await run.SendResponseAsync(request.Request.CreateResponse(reply));
                        break;

                    case WorkflowOutputEvent output:
                        result = output.Data?.ToString();
                        Console.WriteLine(result);
                        break;

                    case WorkflowErrorEvent failure:
                        error = failure.Data?.ToString();
                        break;
                }

                if (emptyDraft) break;   // leave the event loop, not just the switch
            }
        }
        catch (OperationCanceledException) when (!cts.IsCancellationRequested)
        {
            // The watchdog fired: nothing was pending.
        }

        if (!answered)
            return Fail($"session {session} has no pending approval at checkpoint {latest.CheckpointId} (already decided?).");
        if (emptyDraft)
            return Fail($"session {session} has an empty draft, so there is nothing to approve.");
        if (error is not null)
            return Fail($"checkpoint {latest.CheckpointId} could not be resumed: {error}");
        return result is null
            ? Fail($"session {session} resumed but produced no result.")
            : 0;
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return Fail($"checkpoint for session {session} is missing or unreadable " +
                    $"({ex.GetType().Name}: {ex.Message})");
    }
}

static int Fail(string message, int exitCode = 3)
{
    Console.Error.WriteLine($"RESUME FAILED: {message} Nothing was finalized.");
    return exitCode;
}

string? OptionValue(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

// Positional words, skipping this sample's own switches and the --store value.
string[] PositionalArgs()
{
    List<string> words = [];
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--store") i++;
        else if (!args[i].StartsWith("--", StringComparison.Ordinal)) words.Add(args[i]);
    }
    return [.. words];
}

IHost BuildHost()
{
    // In prepare/resume, the mode words and this sample's own switches are not
    // configuration; a --key=value override (--LlmProvider:Provider=openai) still is.
    string[] configArgs = mode is null
        ? args
        : args.Where(arg => arg.StartsWith("--", StringComparison.Ordinal) && arg.Contains('=')).ToArray();
    HostApplicationBuilder hostBuilder = Host.CreateApplicationBuilder(configArgs);
    hostBuilder.Services.AddLlmChatClient(hostBuilder.Configuration);
    return hostBuilder.Build();
}

// ---------------------------------------------------------------
// The decision, typed. Same pattern as Ch14.ApprovalWorkflow's
// ReviewDecision, minus "revise" (this workflow has no revision loop).
// ---------------------------------------------------------------

/// <summary>The only two outcomes a reviewer can choose here.</summary>
internal enum ApprovalDecision { Approve, Reject }

internal static class ApprovalDecisions
{
    /// <summary>The reply that routes to the rejection handler.</summary>
    public const string RejectReply = "reject";

    /// <summary>
    /// Accepts exactly "approve" or "reject" (case and surrounding spaces
    /// ignored). Everything else, including empty input, is not a decision.
    /// </summary>
    public static bool TryParse(string? input, out ApprovalDecision decision)
    {
        switch (input?.Trim().ToLowerInvariant())
        {
            case "approve": decision = ApprovalDecision.Approve; return true;
            case "reject": decision = ApprovalDecision.Reject; return true;
            default: decision = default; return false;
        }
    }

    /// <summary>
    /// Builds the RequestPort reply for a decision: the draft itself on approve,
    /// the reject word on reject. Fails closed: approving a null, empty or
    /// whitespace draft returns false and no reply, so nothing is approved by default.
    /// </summary>
    public static bool TryCreateReply(ApprovalDecision decision, string? draft, [NotNullWhen(true)] out string? reply)
    {
        reply = decision switch
        {
            ApprovalDecision.Reject => RejectReply,
            ApprovalDecision.Approve when !string.IsNullOrWhiteSpace(draft) => draft,
            _ => null,
        };
        return reply is not null;
    }
}

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

/// <summary>Ends a rejected review: nothing is finalized.</summary>
[YieldsOutput(typeof(string))]
internal sealed class Rejection() : Executor<string>("rejection")
{
    public override async ValueTask HandleAsync(
        string decision, IWorkflowContext context, CancellationToken ct)
        => await context.YieldOutputAsync("[REJECTED] The draft was not finalized.", ct);
}

/// <summary>
/// A fake IChatClient for --offline and --self-test: it returns one fixed
/// reply (normally <see cref="Draft"/>), so the workflow runs deterministically
/// without a model.
/// </summary>
internal sealed class ScriptedChatClient(string reply) : IChatClient
{
    public const string Draft =
        "Code review catches defects while they are still cheap to fix. It spreads " +
        "knowledge of the codebase across the team. It also keeps style and design " +
        "decisions consistent from one change to the next.";

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, reply);
        await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

// ---------------------------------------------------------------
// --self-test: a real two-process resume. Each prepare and resume runs
// as its own child process (dotnet exec of this same assembly, with
// --offline) against a temp folder, so the only thing the two runs
// share is the checkpoint store on disk.
// ---------------------------------------------------------------
internal static class CrossProcessSelfTest
{
    private static int s_passed;
    private static int s_total;

    public static int Run()
    {
        Console.WriteLine("=== Chapter 13: Checkpointed Approval — cross-process self-test ===\n");
        string root = Path.Combine(Path.GetTempPath(), $"ch13-checkpoint-selftest-{Guid.NewGuid():N}");

        try
        {
            // 0. The typed decision and the reply it produces (in process).
            Check("only \"approve\" and \"reject\" parse as decisions",
                ApprovalDecisions.TryParse(" APPROVE ", out ApprovalDecision a) && a == ApprovalDecision.Approve &&
                ApprovalDecisions.TryParse("reject", out ApprovalDecision r) && r == ApprovalDecision.Reject &&
                !ApprovalDecisions.TryParse("", out _) && !ApprovalDecisions.TryParse(null, out _) &&
                !ApprovalDecisions.TryParse("approved", out _) && !ApprovalDecisions.TryParse("yes", out _));
            Check("approve + empty or null draft -> no reply (fail closed)",
                !ApprovalDecisions.TryCreateReply(ApprovalDecision.Approve, "", out _) &&
                !ApprovalDecisions.TryCreateReply(ApprovalDecision.Approve, "   ", out _) &&
                !ApprovalDecisions.TryCreateReply(ApprovalDecision.Approve, null, out _));
            Check("approve + draft -> the draft; reject -> the reject word",
                ApprovalDecisions.TryCreateReply(ApprovalDecision.Approve, "text", out string? yes) && yes == "text" &&
                ApprovalDecisions.TryCreateReply(ApprovalDecision.Reject, null, out string? no) &&
                no == ApprovalDecisions.RejectReply);

            // The no-argument demo says its decision is simulated, and refuses an empty draft.
            Child demo = RunChild();
            Check("demo mode labels the approval as SIMULATED and completes",
                demo.ExitCode == 0 && demo.Output.Contains("SIMULATED human decision: approve") &&
                demo.Output.Contains("[APPROVED]") && demo.Output.Contains(ScriptedChatClient.Draft));
            Child emptyDemo = RunChild("--empty-draft");
            Check("demo mode with an empty draft approves nothing",
                emptyDemo.ExitCode != 0 && emptyDemo.Output.Contains("DEMO STOPPED") &&
                !emptyDemo.Output.Contains("[APPROVED]"));

            // 1. prepare (process A), then resume + approve (process B).
            string approveStore = Path.Combine(root, "approve");
            Child prepare = RunChild("prepare", "--store", approveStore);
            string? session = ValueFrom(prepare, "Session:");
            Check("prepare exits 0 and prints a session id", prepare.ExitCode == 0 && session is not null);
            Check("prepare stopped at the approval point (nothing finalized)",
                prepare.Output.Contains("awaiting approval") && !prepare.Output.Contains("[APPROVED]"));
            Check("checkpoint files written to the store",
                Directory.Exists(approveStore) && Directory.EnumerateFiles(approveStore).Any());

            Child resume = RunChild("resume", session ?? "none", "approve", "--store", approveStore);
            Check("resume runs in a different process", resume.Pid != prepare.Pid && resume.Pid != Environment.ProcessId);
            Check("resume loads the checkpoint prepare persisted",
                ValueFrom(prepare, "Checkpoint:") is { } saved && resume.Output.Contains($"from checkpoint {saved}"));
            Check("executor state crossed the process boundary",
                resume.Output.Contains("restored revision count: 1"));
            Check("resume re-emits the approval request", resume.Output.Contains("re-emitted in a new process"));
            Check("resume + approve -> [APPROVED] with the drafted text",
                resume.ExitCode == 0 && resume.Output.Contains("[APPROVED]") &&
                resume.Output.Contains(ScriptedChatClient.Draft));

            if (resume.ExitCode != 0) Console.WriteLine(Indent(prepare.Output + resume.Output));

            Child again = RunChild("resume", session ?? "none", "approve", "--store", approveStore);
            Check("resuming an already decided session fails closed", FailedClosed(again, "no pending approval"));

            // 2. A fresh session resumed with reject is not finalized.
            string rejectStore = Path.Combine(root, "reject");
            Child prepare2 = RunChild("prepare", "--store", rejectStore);
            Child reject = RunChild("resume", ValueFrom(prepare2, "Session:") ?? "none", "reject", "--store", rejectStore);
            Check("resume + reject -> [REJECTED], nothing approved",
                reject.ExitCode == 0 && reject.Output.Contains("[REJECTED]") && !reject.Output.Contains("[APPROVED]"));

            // 3. Failure cases fail closed: non-zero exit, a clear message, no result.
            string emptyStore = Path.Combine(root, "empty-draft");
            Child prepare3 = RunChild("prepare", "--store", emptyStore, "--empty-draft");
            Child emptyResume = RunChild("resume", ValueFrom(prepare3, "Session:") ?? "none", "approve", "--store", emptyStore);
            Check("resume + approve of an empty draft fails closed", FailedClosed(emptyResume, "empty draft"));

            Child unknown = RunChild("resume", Guid.NewGuid().ToString("N"), "approve", "--store", approveStore);
            Check("unknown session fails closed", FailedClosed(unknown, "no checkpoint for session"));

            Child noStore = RunChild("resume", session ?? "none", "approve", "--store", Path.Combine(root, "missing"));
            Check("missing store folder fails closed", FailedClosed(noStore, "no checkpoint store"));

            Child badDecision = RunChild("resume", session ?? "none", "maybe", "--store", approveStore);
            Check("unknown decision fails closed", FailedClosed(badDecision, "unknown decision"));

            // The file store keeps an index (index.jsonl) plus one file per checkpoint.
            Child corruptBody = ResumeAfterDamage(root, "corrupt-body", store =>
            {
                foreach (string body in CheckpointBodies(store))
                    File.WriteAllText(body, "{ this is not a checkpoint");
            });
            Check("corrupted checkpoint file fails closed", FailedClosed(corruptBody, "missing or unreadable"));

            Child corruptIndex = ResumeAfterDamage(root, "corrupt-index", store =>
                File.WriteAllText(Path.Combine(store, "index.jsonl"), "{ this is not an index"));
            Check("corrupted checkpoint index fails closed", FailedClosed(corruptIndex, "missing or unreadable"));

            Child deletedBody = ResumeAfterDamage(root, "deleted-body", store =>
            {
                foreach (string body in CheckpointBodies(store))
                    File.Delete(body);
            });
            Check("deleted checkpoint file fails closed", FailedClosed(deletedBody, "missing or unreadable"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    /// <summary>prepare into a fresh store, damage it, then try to resume it.</summary>
    private static Child ResumeAfterDamage(string root, string name, Action<string> damage)
    {
        string store = Path.Combine(root, name);
        string session = ValueFrom(RunChild("prepare", "--store", store), "Session:") ?? "none";
        damage(store);
        return RunChild("resume", session, "approve", "--store", store);
    }

    private static IEnumerable<string> CheckpointBodies(string store) =>
        Directory.GetFiles(store).Where(file => !Path.GetFileName(file).Equals("index.jsonl", StringComparison.Ordinal));

    private static bool FailedClosed(Child child, string message)
    {
        string? reason = child.Output.Split('\n')
            .FirstOrDefault(line => line.StartsWith("RESUME FAILED", StringComparison.Ordinal));
        Console.WriteLine(Indent(reason ?? child.Output));
        return child.ExitCode != 0 && child.Output.Contains(message) && !child.Output.Contains("[APPROVED]");
    }

    private static void Check(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }

    private static string? ValueFrom(Child child, string label) =>
        child.Output.Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith(label, StringComparison.Ordinal))?
            [label.Length..].Trim();

    private static string Indent(string text) =>
        string.Join('\n', text.Split('\n').Select(line => "        " + line.TrimEnd()));

    private sealed record Child(int ExitCode, string Output, int Pid);

    /// <summary>Runs this assembly again as a separate process: dotnet exec &lt;dll&gt; args --offline.</summary>
    private static Child RunChild(params string[] childArgs)
    {
        ProcessStartInfo psi = new(DotnetHost())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(typeof(CrossProcessSelfTest).Assembly.Location);
        foreach (string arg in childArgs) psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add("--offline");

        using Process process = Process.Start(psi)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(120)))
        {
            process.Kill(entireProcessTree: true);
            return new Child(-1, "timed out", process.Id);
        }
        return new Child(process.ExitCode, stdout.Result + stderr.Result, process.Id);
    }

    private static string DotnetHost()
    {
        string? current = Environment.ProcessPath;
        if (current is not null &&
            Path.GetFileNameWithoutExtension(current).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return current;
        string? host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return !string.IsNullOrEmpty(host) && File.Exists(host) ? host : "dotnet";
    }
}
