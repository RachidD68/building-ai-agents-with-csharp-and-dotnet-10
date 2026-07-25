// ============================================================================
// Exercise 02 — Review Pipeline (Intermediate)
// Length-based conditional workflow: a classifier measures each submission,
// then AddSwitch routes short/medium/long input down different review paths:
//   Input → LengthClassifier → [QuickReview | StandardReview | DeepReview]
// Mirrors the AddSwitch usage in src/Ch13.Workflows/Ch13.ConditionalWorkflow.
// FULLY STANDALONE.
// ============================================================================

using Microsoft.Agents.AI.Workflows;

Console.WriteLine("=== Exercise: Review Pipeline — Length Router ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ---------------------------------------------------------------
// Review submissions of different sizes
// ---------------------------------------------------------------
string[] submissions =
[
    // Short (< 20 words) → quick review branch
    "Fix the null check in PaymentService.Validate and add a regression test.",

    // Medium (20-59 words) → standard review branch
    """
    Refactor the invoice exporter to stream rows instead of buffering the
    full result set in memory. The change introduces an IAsyncEnumerable
    pipeline, adds a unit test for the empty-batch case, and updates the
    exporter documentation to describe the new backpressure behavior.
    """,

    // Long (>= 60 words) → deep review branch
    """
    Replace the legacy order-fulfillment saga with a checkpointed workflow.
    The change moves state transitions out of the OrdersController into a
    dedicated orchestrator, introduces compensation steps for payment capture
    and inventory reservation, migrates the outbox table to the new schema,
    rewrites the retry policy around exponential backoff with jitter, adds
    integration tests for the crash-recovery path, and updates the runbook
    with the new operational dashboards and alert thresholds for on-call.
    """
];

foreach ((int i, string submission) in submissions.Index())
{
    Console.WriteLine($"--- Submission {i + 1} ---");
    Console.WriteLine(submission.Trim());
    Console.WriteLine();

    // Executor ids need only be unique per workflow instance; the loop index
    // is deterministic run to run.
    Workflow workflow = BuildLengthRoutedWorkflow($"R{i}");

    Run run = await InProcessExecution.RunAsync(
        workflow, submission, sessionId: Guid.NewGuid().ToString(), cts.Token);

    foreach (WorkflowEvent evt in run.OutgoingEvents)
    {
        if (evt is WorkflowOutputEvent output)
        {
            Console.WriteLine($"  Result: {output.Data}");
        }
    }
    Console.WriteLine();
}

Console.WriteLine("=== Exercise complete! ===");

// ---------------------------------------------------------------
// Workflow builder (fresh instance per run)
// ---------------------------------------------------------------

/// <summary>
/// Builds a workflow that routes a submission by length using AddSwitch:
/// short → quick review, long → deep review, everything else → standard.
/// </summary>
Workflow BuildLengthRoutedWorkflow(string suffix)
{
    ExecutorBinding classifier = new LengthClassifier($"classifier-{suffix}").BindExecutor();
    ExecutorBinding quick = new QuickReviewHandler($"quick-{suffix}").BindExecutor();
    ExecutorBinding standard = new StandardReviewHandler($"standard-{suffix}").BindExecutor();
    ExecutorBinding deep = new DeepReviewHandler($"deep-{suffix}").BindExecutor();

    return new WorkflowBuilder(classifier)
        .AddSwitch(classifier, sw => sw
            .AddCase<LengthResult>(
                r => r is not null && r.Category == LengthCategory.Short,
                [quick])
            .AddCase<LengthResult>(
                r => r is not null && r.Category == LengthCategory.Long,
                [deep])
            .WithDefault([standard]))
        .WithOutputFrom(quick, standard, deep)
        .WithName($"Review-{suffix}")
        .Build();
}

// ---------------------------------------------------------------
// Executors
// ---------------------------------------------------------------

/// <summary>Counts words and buckets the submission as short, medium, or long.</summary>
internal sealed class LengthClassifier(string id) : Executor<string, LengthResult>(id)
{
    public override ValueTask<LengthResult> HandleAsync(
        string input, IWorkflowContext context, CancellationToken ct)
    {
        int words = input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        LengthCategory category = words switch
        {
            < 20 => LengthCategory.Short,
            < 60 => LengthCategory.Medium,
            _ => LengthCategory.Long
        };

        Console.WriteLine($"  [Classifier] {words} words → {category}");
        return ValueTask.FromResult(new LengthResult(input, words, category));
    }
}

/// <summary>Fast-tracks short submissions through a lightweight review.</summary>
internal sealed class QuickReviewHandler(string id) : Executor<LengthResult, string>(id)
{
    public override ValueTask<string> HandleAsync(
        LengthResult result, IWorkflowContext context, CancellationToken ct)
    {
        string response = $"QUICK REVIEW ({result.WordCount} words): " +
                          "single reviewer, style checklist only — approved on green CI.";
        Console.WriteLine("  [QuickReview] Fast-tracked");
        return ValueTask.FromResult(response);
    }
}

/// <summary>Runs the standard review path for medium submissions.</summary>
internal sealed class StandardReviewHandler(string id) : Executor<LengthResult, string>(id)
{
    public override ValueTask<string> HandleAsync(
        LengthResult result, IWorkflowContext context, CancellationToken ct)
    {
        string response = $"STANDARD REVIEW ({result.WordCount} words): " +
                          "one reviewer plus automated static analysis and test-coverage gate.";
        Console.WriteLine("  [StandardReview] Routed to the regular queue");
        return ValueTask.FromResult(response);
    }
}

/// <summary>Escalates long submissions to a deep, multi-reviewer pass.</summary>
internal sealed class DeepReviewHandler(string id) : Executor<LengthResult, string>(id)
{
    public override ValueTask<string> HandleAsync(
        LengthResult result, IWorkflowContext context, CancellationToken ct)
    {
        string response = $"DEEP REVIEW ({result.WordCount} words): " +
                          "two reviewers, architecture sign-off, and a design-doc link required.";
        Console.WriteLine("  [DeepReview] Escalated for a thorough pass");
        return ValueTask.FromResult(response);
    }
}

// ---------------------------------------------------------------
// Types
// ---------------------------------------------------------------

/// <summary>Length bucket a submission falls into.</summary>
internal enum LengthCategory
{
    /// <summary>Fewer than 20 words.</summary>
    Short,

    /// <summary>20 to 59 words.</summary>
    Medium,

    /// <summary>60 words or more.</summary>
    Long
}

/// <summary>
/// Carries the original submission together with its measured length
/// through the workflow.
/// </summary>
/// <param name="Submission">The original review submission.</param>
/// <param name="WordCount">Number of whitespace-separated words.</param>
/// <param name="Category">The length bucket used for routing.</param>
internal sealed record LengthResult(string Submission, int WordCount, LengthCategory Category);
