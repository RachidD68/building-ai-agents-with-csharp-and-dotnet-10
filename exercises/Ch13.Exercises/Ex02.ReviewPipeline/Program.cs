// ============================================================================
// Exercise 02 — Review Pipeline (Intermediate)
// Code review workflow with conditional routing:
// CodeAnalyzer → SecurityScanner → (issues? → FixSuggestion : Approval)
// Uses class-based executors (Executor<TInput, TOutput>). FULLY STANDALONE.
// ============================================================================

using Microsoft.Agents.AI.Workflows;

Console.WriteLine("=== Exercise: Review Pipeline Workflow ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ---------------------------------------------------------------
// Test with different code samples
// ---------------------------------------------------------------
string[] samples =
[
    """
    // TODO: refactor this later
    var service = new MyService();
    var result = service.Process();
    """,
    """
    /// <summary>Processes the request.</summary>
    public async Task<Result> ProcessAsync(CancellationToken ct)
    {
        return await _handler.HandleAsync(ct);
    }
    """,
    """
    var password = "admin123";
    var sql = "SELECT * FROM users WHERE name='" + userName + "'";
    """
];

for (int i = 0; i < samples.Length; i++)
{
    Console.WriteLine($"\n--- Code Sample {i + 1} ---");
    Console.WriteLine(samples[i].Trim());
    Console.WriteLine();

    Workflow workflow = BuildReviewWorkflow(i.ToString());

    Run run = await InProcessExecution.RunAsync(
        workflow, samples[i], sessionId: Guid.NewGuid().ToString(), cts.Token);

    foreach (WorkflowEvent evt in run.OutgoingEvents)
    {
        if (evt is WorkflowOutputEvent output)
        {
            Console.WriteLine($"  Result: {output.Data}");
        }
    }
}

Console.WriteLine("\n=== Exercise complete! ===");

// ---------------------------------------------------------------
// Build a fresh review workflow with conditional routing.
// ---------------------------------------------------------------
Workflow BuildReviewWorkflow(string suffix)
{
    ExecutorBinding analyzer = new CodeAnalyzerExecutor($"analyzer-{suffix}").BindExecutor();
    ExecutorBinding scanner = new SecurityScannerExecutor($"scanner-{suffix}").BindExecutor();
    ExecutorBinding fix = new FixSuggestionExecutor($"fix-{suffix}").BindExecutor();
    ExecutorBinding approve = new ApprovalExecutor($"approve-{suffix}").BindExecutor();

    return new WorkflowBuilder(analyzer)
        .AddEdge(analyzer, scanner)
        .AddEdge<string>(scanner, fix,
            r => r is not null && r.StartsWith("ISSUES_FOUND", StringComparison.Ordinal))
        .AddEdge<string>(scanner, approve,
            r => r is null || !r.StartsWith("ISSUES_FOUND", StringComparison.Ordinal))
        .WithOutputFrom(fix, approve)
        .WithName($"Review-{suffix}")
        .Build();
}

// ---------------------------------------------------------------
// Executors
// ---------------------------------------------------------------

/// <summary>Flags simple style issues in the code.</summary>
internal sealed class CodeAnalyzerExecutor(string id) : Executor<string, string>(id)
{
    public override ValueTask<string> HandleAsync(
        string input, IWorkflowContext context, CancellationToken ct)
    {
        List<string> issues = [];
        if (input.Contains("TODO", StringComparison.OrdinalIgnoreCase))
            issues.Add("STYLE: Unresolved TODO comment found");
        if (input.Contains("var ", StringComparison.Ordinal) && input.Contains("new ", StringComparison.Ordinal))
            issues.Add("STYLE: Consider using target-typed new");
        if (!input.Contains("///", StringComparison.Ordinal))
            issues.Add("STYLE: Missing XML documentation comments");

        string result = issues.Count > 0
            ? $"ISSUES_FOUND|{string.Join(";", issues)}|CODE:{input}"
            : $"NO_ISSUES|CODE:{input}";

        Console.WriteLine($"  [CodeAnalyzer] Found {issues.Count} style issues");
        return ValueTask.FromResult(result);
    }
}

/// <summary>Flags simple security issues and merges them with any style issues.</summary>
internal sealed class SecurityScannerExecutor(string id) : Executor<string, string>(id)
{
    public override ValueTask<string> HandleAsync(
        string input, IWorkflowContext context, CancellationToken ct)
    {
        List<string> issues = [];
        string code = input.Contains("CODE:")
            ? input[(input.IndexOf("CODE:", StringComparison.Ordinal) + 5)..]
            : input;

        if (code.Contains("password", StringComparison.OrdinalIgnoreCase) && code.Contains("\"", StringComparison.Ordinal))
            issues.Add("SECURITY: Hardcoded password detected");
        if (code.Contains("sql", StringComparison.OrdinalIgnoreCase) && code.Contains("+", StringComparison.Ordinal))
            issues.Add("SECURITY: Possible SQL injection via string concatenation");

        string existing = input.StartsWith("ISSUES_FOUND|", StringComparison.Ordinal)
            ? input
            : (issues.Count > 0 ? "ISSUES_FOUND|" : "NO_ISSUES|");

        if (issues.Count > 0 && !existing.StartsWith("ISSUES_FOUND|", StringComparison.Ordinal))
        {
            existing = $"ISSUES_FOUND|{string.Join(";", issues)}|CODE:{code}";
        }
        else if (issues.Count > 0)
        {
            int codeIdx = existing.IndexOf("|CODE:", StringComparison.Ordinal);
            if (codeIdx > 0)
            {
                existing = existing[..codeIdx] + ";" + string.Join(";", issues) + existing[codeIdx..];
            }
        }

        Console.WriteLine($"  [SecurityScanner] Found {issues.Count} security issues");
        return ValueTask.FromResult(existing);
    }
}

/// <summary>Generates fix suggestions for failing reviews and yields the result.</summary>
[YieldsOutput(typeof(string))]
internal sealed class FixSuggestionExecutor(string id) : Executor<string>(id)
{
    public override async ValueTask HandleAsync(
        string input, IWorkflowContext context, CancellationToken ct)
    {
        string result = $"REVIEW FAILED - Issues require attention:\n{input.Replace("|", "\n  - ").Replace(";", "\n  - ")}";
        Console.WriteLine("  [FixSuggestion] Generated fix suggestions");
        await context.YieldOutputAsync(result, ct);
    }
}

/// <summary>Approves clean code and yields the result.</summary>
[YieldsOutput(typeof(string))]
internal sealed class ApprovalExecutor(string id) : Executor<string>(id)
{
    public override async ValueTask HandleAsync(
        string input, IWorkflowContext context, CancellationToken ct)
    {
        const string result = "REVIEW PASSED - Code meets quality and security standards. Approved for merge.";
        Console.WriteLine("  [Approval] Code approved!");
        await context.YieldOutputAsync(result, ct);
    }
}
