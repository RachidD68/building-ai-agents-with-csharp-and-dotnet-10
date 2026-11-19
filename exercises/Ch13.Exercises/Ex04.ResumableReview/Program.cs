// ============================================================================
// Exercise 04 — Resumable Review (Challenge)
// Extends Exercise 02's review pipeline with a CheckpointManager so a run can
// be killed and resumed mid-way. The workflow analyzes code, then pauses on a
// RequestPort for a human sign-off. We capture the checkpoint at that pause,
// "crash," and resume from the checkpoint to finish. FULLY STANDALONE.
//
//   Analyzer → SecurityScanner → [RequestPort: human sign-off] → Verdict
//
// Modes:
//   dotnet run                            in-memory demo, both runs in one process;
//                                         the sign-off is SIMULATED (a fixed decision)
//   dotnet run -- prepare                 run to the sign-off, persist, exit
//   dotnet run -- resume <session> <approve|reject>
//                                         a NEW process loads the checkpoint
//                                         from disk and finishes the review
//   dotnet run -- --self-test             prepare and resume as two separate
//                                         child processes, plus failure cases
// prepare and resume accept --store <folder> (default: a folder under %TEMP%).
// Every mode fails closed: a sign-off request with empty findings is never
// approved (--empty-findings makes the scanner return nothing, to show it).
// ============================================================================

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

if (args.Contains("--self-test")) return CrossProcessSelfTest.Run();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

string codeUnderReview = """
    var password = "admin123";
    var sql = "SELECT * FROM users WHERE name='" + userName + "'";
    """;

RequestPort signOffPort = RequestPort.Create<string, string>("human-sign-off");

// Fault injection for the self-test: the scanner returns no findings at all.
bool emptyFindings = args.Contains("--empty-findings");

Workflow BuildReviewWorkflow()
{
    ExecutorBinding analyzer = new CodeAnalyzerExecutor().BindExecutor();
    ExecutorBinding scanner = new SecurityScannerExecutor(emptyFindings).BindExecutor();
    ExecutorBinding signOff = signOffPort.BindAsExecutor();
    ExecutorBinding verdict = new VerdictExecutor().BindExecutor();

    return new WorkflowBuilder(analyzer)
        .AddEdge(analyzer, scanner)
        .AddEdge(scanner, signOff)
        .AddEdge(signOff, verdict)
        .WithOutputFrom(verdict)
        .WithName("ResumableReview")
        .Build();
}

string storePath = OptionValue("--store")
    ?? Path.Combine(Path.GetTempPath(), "AgenticAI", "Ex04.ResumableReview");

string[] positional = PositionalArgs();
switch (positional.FirstOrDefault())
{
    case null:
        break; // the in-memory demo below

    case "prepare":
        return await PrepareAsync();

    case "resume" when positional.Length >= 3:
        return await ResumeAsync(positional[1], positional[2]);

    default:
        Console.Error.WriteLine("Usage: [prepare | resume <session-id> <approve|reject>] [--store <folder>] | --self-test");
        return 2;
}

Console.WriteLine("=== Exercise: Resumable Review ===\n");

// One CheckpointManager shared by both the first run and the resumed run.
CheckpointManager checkpoints = CheckpointManager.CreateInMemory();
string sessionId = Guid.NewGuid().ToString();

// ---------------------------------------------------------------
// Run 1: analyze, scan, then pause for sign-off — and "crash."
// ---------------------------------------------------------------
Console.WriteLine("--- Run 1: review until sign-off, then crash ---\n");
Console.WriteLine(codeUnderReview);
Console.WriteLine();

CheckpointInfo? pauseCheckpoint = null;

StreamingRun firstRun = await InProcessExecution.RunStreamingAsync(
    BuildReviewWorkflow(), codeUnderReview, checkpoints, sessionId, cts.Token);

await foreach (WorkflowEvent evt in firstRun.WatchStreamAsync(cts.Token))
{
    if (evt is SuperStepCompletedEvent step && step.CompletionInfo?.Checkpoint is { } checkpoint)
    {
        pauseCheckpoint = checkpoint;
    }

    if (evt is RequestInfoEvent request && request.Request.TryGetDataAs<string>(out string? findings))
    {
        Console.WriteLine($"Findings awaiting sign-off:\n{findings}\n");
        Console.WriteLine($"Captured checkpoint: {pauseCheckpoint?.CheckpointId}");
        Console.WriteLine(">>> Simulating crash before the reviewer signs off. <<<\n");
        break;
    }
}

// ---------------------------------------------------------------
// Run 2: resume from the checkpoint and sign off.
//
// Nobody is at the keyboard in this demo, so the decision is
// SIMULATED: a fixed, typed SignOffDecision, printed as such. The
// prepare/resume modes take a real decision from the command line.
// ---------------------------------------------------------------
const SignOffDecision SimulatedDecision = SignOffDecision.Approve;

Console.WriteLine("--- Run 2: resume from checkpoint, sign off ---\n");

StreamingRun resumedRun = await InProcessExecution.ResumeStreamingAsync(
    BuildReviewWorkflow(), pauseCheckpoint!, checkpoints, cts.Token);

bool stopped = false;
await foreach (WorkflowEvent evt in resumedRun.WatchStreamAsync(cts.Token))
{
    switch (evt)
    {
        case RequestInfoEvent request
            when request.Request.TryGetDataAs<string>(out string? findings):
            Console.WriteLine("Sign-off request re-emitted after restart.");
            Console.WriteLine($"SIMULATED reviewer decision: {SimulatedDecision.ToString().ToLowerInvariant()} " +
                              "(no reviewer in this demo; use prepare/resume to decide yourself).\n");

            // Fail closed: signing off needs findings the reviewer could have read.
            if (!SignOffDecisions.TryCreateReply(SimulatedDecision, findings, out string? reply))
            {
                stopped = true;
                break;
            }
            await resumedRun.SendResponseAsync(request.Request.CreateResponse(reply));
            break;

        case WorkflowOutputEvent output:
            Console.WriteLine(output.Data);
            break;
    }

    if (stopped) break;   // leave the event loop, not just the switch
}

if (stopped)
{
    Console.Error.WriteLine("DEMO STOPPED: the sign-off request has empty findings, so there is nothing to approve. " +
                            "Review left pending; nothing was signed off.");
    return 2;
}

Console.WriteLine("\n=== Exercise complete! ===");
return 0;

// ---------------------------------------------------------------
// prepare: process 1 runs until the sign-off request, persists the
// checkpoint to disk, prints the session id and exits.
// ---------------------------------------------------------------
async Task<int> PrepareAsync()
{
    Console.WriteLine("=== Exercise: Resumable Review — prepare ===\n");

    Directory.CreateDirectory(storePath);
    using FileSystemJsonCheckpointStore store = new(new DirectoryInfo(storePath));
    CheckpointManager durable = CheckpointManager.CreateJson(store);
    string session = Guid.NewGuid().ToString("N");

    CheckpointInfo? latest = null;
    bool paused = false;

    await using (StreamingRun run = await InProcessExecution.RunStreamingAsync(
        BuildReviewWorkflow(), codeUnderReview, durable, session, cts.Token))
    {
        await foreach (WorkflowEvent evt in run.WatchStreamAsync(cts.Token))
        {
            if (evt is RequestInfoEvent request && request.Request.TryGetDataAs<string>(out string? findings))
            {
                Console.WriteLine($"Findings awaiting sign-off:\n{findings}\n");
                paused = true;
            }

            // The superstep that raised the request ends with a checkpoint that
            // holds the pending sign-off. Once it is on disk, walk away.
            if (evt is SuperStepCompletedEvent step && step.CompletionInfo?.Checkpoint is { } cp)
            {
                latest = cp;
                if (paused) break;
            }
        }
    }

    if (!paused || latest is null)
    {
        Console.Error.WriteLine("PREPARE FAILED: the workflow never reached the sign-off point.");
        return 1;
    }

    Console.WriteLine($"Store:      {storePath}");
    Console.WriteLine($"Session:    {session}");
    Console.WriteLine($"Checkpoint: {latest.CheckpointId}");
    Console.WriteLine($"\nResume in a new process:  dotnet run -- resume {session} approve");
    return 0;
}

// ---------------------------------------------------------------
// resume: process 2 loads the latest checkpoint for the session from
// the same folder, answers the re-emitted sign-off and finishes. Any
// problem with the store or the checkpoint fails closed: nothing is
// signed off and the exit code is non-zero.
// ---------------------------------------------------------------
async Task<int> ResumeAsync(string session, string decisionArg)
{
    Console.WriteLine("=== Exercise: Resumable Review — resume ===\n");

    if (!SignOffDecisions.TryParse(decisionArg, out SignOffDecision decision))
        return Fail($"unknown decision \"{decisionArg}\" (use approve or reject).", 2);

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

        // The latest checkpoint of a session that was already signed off has no
        // request left to replay, and watching it would wait forever. Give the
        // re-emitted request a bounded window; once it arrives, disarm the timer.
        using CancellationTokenSource watchdog = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        watchdog.CancelAfter(TimeSpan.FromSeconds(30));

        bool answered = false;
        string? verdict = null;
        string? error = null;
        try
        {
            await using StreamingRun run = await InProcessExecution.ResumeStreamingAsync(
                BuildReviewWorkflow(), latest, durable, cts.Token);

            await foreach (WorkflowEvent evt in run.WatchStreamAsync(watchdog.Token))
            {
                switch (evt)
                {
                    case RequestInfoEvent request when request.Request.TryGetDataAs<string>(out string? findings):
                        watchdog.CancelAfter(Timeout.InfiniteTimeSpan);
                        answered = true;
                        Console.WriteLine($"Sign-off request re-emitted in a new process:\n{findings}\n");
                        Console.WriteLine($"Reviewer decision: {decisionArg}\n");
                        // Fail closed: signing off needs findings the reviewer could have read.
                        if (!SignOffDecisions.TryCreateReply(decision, findings, out string? reply))
                            return Fail($"session {session} has empty findings at checkpoint {latest.CheckpointId}, " +
                                        "so there is nothing to approve.");
                        await run.SendResponseAsync(request.Request.CreateResponse(reply));
                        break;

                    case WorkflowOutputEvent output:
                        verdict = output.Data?.ToString();
                        Console.WriteLine(verdict);
                        break;

                    case WorkflowErrorEvent failure:
                        error = failure.Data?.ToString();
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (!cts.IsCancellationRequested)
        {
            // The watchdog fired: nothing was pending.
        }

        if (!answered)
            return Fail($"session {session} has no pending sign-off at checkpoint {latest.CheckpointId} (already decided?).");
        if (error is not null)
            return Fail($"checkpoint {latest.CheckpointId} could not be resumed: {error}");
        return verdict is null
            ? Fail($"session {session} resumed but produced no verdict.")
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
    Console.Error.WriteLine($"RESUME FAILED: {message} Nothing was signed off.");
    return exitCode;
}

string? OptionValue(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

// The mode word and its values, without the --switches (and --store's folder).
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

// ---------------------------------------------------------------
// The decision, typed: the only two outcomes a reviewer can choose.
// ---------------------------------------------------------------

internal enum SignOffDecision { Approve, Reject }

internal static class SignOffDecisions
{
    /// <summary>The replies the sign-off port passes on to the verdict step.</summary>
    public const string ApprovedReply = "approved";
    public const string RejectedReply = "rejected";

    /// <summary>
    /// Accepts exactly "approve" or "reject" (case and surrounding spaces
    /// ignored). Everything else, including empty input, is not a decision.
    /// </summary>
    public static bool TryParse(string? input, out SignOffDecision decision)
    {
        switch (input?.Trim().ToLowerInvariant())
        {
            case "approve": decision = SignOffDecision.Approve; return true;
            case "reject": decision = SignOffDecision.Reject; return true;
            default: decision = default; return false;
        }
    }

    /// <summary>
    /// Builds the reply for a decision. Fails closed: approving a sign-off
    /// request whose findings are null, empty or whitespace returns false and
    /// no reply, because the reviewer had nothing to acknowledge.
    /// </summary>
    public static bool TryCreateReply(SignOffDecision decision, string? findings, [NotNullWhen(true)] out string? reply)
    {
        reply = decision switch
        {
            SignOffDecision.Reject => RejectedReply,
            SignOffDecision.Approve when !string.IsNullOrWhiteSpace(findings) => ApprovedReply,
            _ => null,
        };
        return reply is not null;
    }
}

// ---------------------------------------------------------------
// Executors
// ---------------------------------------------------------------

/// <summary>Flags simple style issues.</summary>
internal sealed class CodeAnalyzerExecutor() : Executor<string, string>("analyzer")
{
    public override ValueTask<string> HandleAsync(
        string input, IWorkflowContext context, CancellationToken ct)
    {
        List<string> issues = [];
        if (input.Contains("TODO", StringComparison.OrdinalIgnoreCase))
            issues.Add("STYLE: Unresolved TODO");
        if (!input.Contains("///", StringComparison.Ordinal))
            issues.Add("STYLE: Missing XML docs");

        Console.WriteLine($"  [Analyzer] {issues.Count} style issue(s)");
        return ValueTask.FromResult(string.Join("; ", issues));
    }
}

/// <summary>Adds security findings to the style findings.</summary>
/// <param name="returnNothing">Self-test fault injection: return no findings at all.</param>
internal sealed class SecurityScannerExecutor(bool returnNothing = false) : Executor<string, string>("scanner")
{
    public override ValueTask<string> HandleAsync(
        string styleFindings, IWorkflowContext context, CancellationToken ct)
    {
        if (returnNothing)
        {
            Console.WriteLine("  [Scanner] returned no findings (--empty-findings)");
            return ValueTask.FromResult(string.Empty);
        }

        List<string> issues = [];
        if (styleFindings.Length > 0)
            issues.Add(styleFindings);
        // The scanner is given the style findings; it adds fixed security notes
        // for this exercise so the output is deterministic.
        issues.Add("SECURITY: Hardcoded password detected");
        issues.Add("SECURITY: Possible SQL injection");

        Console.WriteLine("  [Scanner] 2 security issue(s)");
        return ValueTask.FromResult(string.Join("; ", issues));
    }
}

/// <summary>Formats the final verdict and yields it as workflow output.</summary>
[YieldsOutput(typeof(string))]
internal sealed class VerdictExecutor() : Executor<string>("verdict")
{
    public override async ValueTask HandleAsync(
        string decision, IWorkflowContext context, CancellationToken ct)
    {
        // Only the exact approve reply signs off; anything else blocks the merge.
        string verdict = decision == SignOffDecisions.ApprovedReply
            ? "REVIEW SIGNED OFF — findings acknowledged, merge allowed."
            : "REVIEW BLOCKED — changes required.";
        await context.YieldOutputAsync(verdict, ct);
    }
}

// ---------------------------------------------------------------
// --self-test: a real two-process resume. Each prepare and resume runs
// as its own child process (dotnet exec of this same assembly) against
// a temp folder, so the only thing the two runs share is the disk.
// ---------------------------------------------------------------
internal static class CrossProcessSelfTest
{
    private static int s_passed;
    private static int s_total;

    public static int Run()
    {
        Console.WriteLine("=== Exercise: Resumable Review — cross-process self-test ===\n");
        string root = Path.Combine(Path.GetTempPath(), $"ex04-selftest-{Guid.NewGuid():N}");

        try
        {
            // 0. The typed decision and the reply it produces (in process).
            Check("only \"approve\" and \"reject\" parse as decisions",
                SignOffDecisions.TryParse(" APPROVE ", out SignOffDecision a) && a == SignOffDecision.Approve &&
                SignOffDecisions.TryParse("reject", out SignOffDecision r) && r == SignOffDecision.Reject &&
                !SignOffDecisions.TryParse("", out _) && !SignOffDecisions.TryParse(null, out _) &&
                !SignOffDecisions.TryParse("approved", out _) && !SignOffDecisions.TryParse("yes", out _));
            Check("approve + empty or null findings -> no reply (fail closed)",
                !SignOffDecisions.TryCreateReply(SignOffDecision.Approve, "", out _) &&
                !SignOffDecisions.TryCreateReply(SignOffDecision.Approve, "   ", out _) &&
                !SignOffDecisions.TryCreateReply(SignOffDecision.Approve, null, out _));
            Check("approve + findings -> approved; reject -> rejected",
                SignOffDecisions.TryCreateReply(SignOffDecision.Approve, "SECURITY: x", out string? yes) &&
                yes == SignOffDecisions.ApprovedReply &&
                SignOffDecisions.TryCreateReply(SignOffDecision.Reject, null, out string? no) &&
                no == SignOffDecisions.RejectedReply);

            // The no-argument demo says its decision is simulated, and refuses empty findings.
            Child demo = RunChild();
            Check("demo mode labels the sign-off as SIMULATED and completes",
                demo.ExitCode == 0 && demo.Output.Contains("SIMULATED reviewer decision: approve") &&
                demo.Output.Contains("REVIEW SIGNED OFF"));
            Child emptyDemo = RunChild("--empty-findings");
            Check("demo mode with empty findings signs nothing off",
                emptyDemo.ExitCode != 0 && emptyDemo.Output.Contains("DEMO STOPPED") &&
                !emptyDemo.Output.Contains("REVIEW SIGNED OFF"));

            // 1. prepare (process A) then resume + approve (process B).
            string approveStore = Path.Combine(root, "approve");
            Child prepare = RunChild("prepare", "--store", approveStore);
            string? session = SessionFrom(prepare);
            Check("prepare exits 0 and prints a session id", prepare.ExitCode == 0 && session is not null);
            Check("prepare stopped at the sign-off (no verdict yet)",
                prepare.Output.Contains("awaiting sign-off") && !prepare.Output.Contains("REVIEW "));
            Check("checkpoint files written to the store",
                Directory.Exists(approveStore) && Directory.EnumerateFiles(approveStore, "*", SearchOption.AllDirectories).Any());

            Child resume = RunChild("resume", session ?? "none", "approve", "--store", approveStore);
            Check("resume runs in a different process", resume.Pid != prepare.Pid && resume.Pid != Environment.ProcessId);
            Check("resume loads the checkpoint prepare persisted",
                ValueFrom(prepare, "Checkpoint:") is { } saved && resume.Output.Contains($"from checkpoint {saved}"));
            Check("resume re-emits the sign-off request", resume.Output.Contains("re-emitted in a new process"));
            Check("resume + approve -> REVIEW SIGNED OFF", resume.ExitCode == 0 && resume.Output.Contains("REVIEW SIGNED OFF"));

            if (resume.ExitCode != 0) Console.WriteLine(Indent(prepare.Output + resume.Output));

            Child again = RunChild("resume", session ?? "none", "approve", "--store", approveStore);
            Check("resuming an already signed-off session fails closed", FailedClosed(again, "no pending sign-off"));

            // 2. A fresh session resumed with reject is blocked.
            string rejectStore = Path.Combine(root, "reject");
            Child prepare2 = RunChild("prepare", "--store", rejectStore);
            Child reject = RunChild("resume", SessionFrom(prepare2) ?? "none", "reject", "--store", rejectStore);
            Check("resume + reject -> REVIEW BLOCKED", reject.ExitCode == 0 && reject.Output.Contains("REVIEW BLOCKED"));

            // 3. Failure cases all fail closed: non-zero exit, clear message, no verdict.
            string emptyStore = Path.Combine(root, "empty-findings");
            Child prepare3 = RunChild("prepare", "--store", emptyStore, "--empty-findings");
            Child emptyResume = RunChild("resume", SessionFrom(prepare3) ?? "none", "approve", "--store", emptyStore);
            Check("resume + approve of empty findings fails closed", FailedClosed(emptyResume, "empty findings"));

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
        string session = SessionFrom(RunChild("prepare", "--store", store)) ?? "none";
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
        return child.ExitCode != 0 && child.Output.Contains(message) && !child.Output.Contains("REVIEW SIGNED OFF");
    }

    private static void Check(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }

    private static string? SessionFrom(Child child) => ValueFrom(child, "Session:");

    private static string? ValueFrom(Child child, string label) =>
        child.Output.Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith(label, StringComparison.Ordinal))?
            [label.Length..].Trim();

    private static string Indent(string text) =>
        string.Join('\n', text.Split('\n').Select(line => "        " + line.TrimEnd()));

    private sealed record Child(int ExitCode, string Output, int Pid);

    /// <summary>Runs this assembly again as a separate process: dotnet exec &lt;dll&gt; args.</summary>
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
