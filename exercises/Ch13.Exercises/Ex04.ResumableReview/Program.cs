// ============================================================================
// Exercise 04 — Resumable Review (Challenge)
// Extends Exercise 02's review pipeline with a CheckpointManager so a run can
// be killed and resumed mid-way. The workflow analyzes code, then pauses on a
// RequestPort for a human sign-off. We capture the checkpoint at that pause,
// "crash," and resume from the checkpoint to finish. FULLY STANDALONE.
//
//   Analyzer → SecurityScanner → [RequestPort: human sign-off] → Verdict
// ============================================================================

using Microsoft.Agents.AI.Workflows;

Console.WriteLine("=== Exercise: Resumable Review ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

string codeUnderReview = """
    var password = "admin123";
    var sql = "SELECT * FROM users WHERE name='" + userName + "'";
    """;

// One CheckpointManager shared by both the first run and the resumed run.
CheckpointManager checkpoints = CheckpointManager.CreateInMemory();
string sessionId = Guid.NewGuid().ToString();

RequestPort signOffPort = RequestPort.Create<string, string>("human-sign-off");

Workflow BuildReviewWorkflow()
{
    ExecutorBinding analyzer = new CodeAnalyzerExecutor().BindExecutor();
    ExecutorBinding scanner = new SecurityScannerExecutor().BindExecutor();
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
// Run 2: resume from the checkpoint and approve.
// ---------------------------------------------------------------
Console.WriteLine("--- Run 2: resume from checkpoint, sign off ---\n");

StreamingRun resumedRun = await InProcessExecution.ResumeStreamingAsync(
    BuildReviewWorkflow(), pauseCheckpoint!, checkpoints, cts.Token);

await foreach (WorkflowEvent evt in resumedRun.WatchStreamAsync(cts.Token))
{
    switch (evt)
    {
        case RequestInfoEvent request
            when request.Request.TryGetDataAs<string>(out string? findings):
            Console.WriteLine("Sign-off request re-emitted after restart. Reviewer approves.\n");
            ExternalResponse response = request.Request.CreateResponse("approved");
            await resumedRun.SendResponseAsync(response);
            break;

        case WorkflowOutputEvent output:
            Console.WriteLine(output.Data);
            break;
    }
}

Console.WriteLine("\n=== Exercise complete! ===");

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
internal sealed class SecurityScannerExecutor() : Executor<string, string>("scanner")
{
    public override ValueTask<string> HandleAsync(
        string styleFindings, IWorkflowContext context, CancellationToken ct)
    {
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
        string verdict = decision.Equals("approved", StringComparison.OrdinalIgnoreCase)
            ? "REVIEW SIGNED OFF — findings acknowledged, merge allowed."
            : "REVIEW BLOCKED — changes required.";
        await context.YieldOutputAsync(verdict, ct);
    }
}
