// ============================================================================
// Chapter 12 — Workflow Tests (design principle #6: cover every branch)
// ----------------------------------------------------------------------------
// Workflows are testable: build the graph, run it in-process with inputs that
// exercise each branch, and assert on the streamed events and the final output.
// No live model is involved — the analyzer is a deterministic keyword classifier,
// so the tests are fast and repeatable, exactly what a unit test should be.
//
// This is a plain console assertion harness rather than an xUnit project so it
// drops into the solution with no extra test packages. The pattern — run the
// graph, collect events, assert — transfers directly to xUnit or NUnit: move
// each Case into a [Fact] and replace Check(...) with Assert.
//
// Exit code 0 means every assertion passed; non-zero means a failure.
// ============================================================================

using Microsoft.Agents.AI.Workflows;

int failures = 0;

Console.WriteLine("=== Chapter 12: Workflow Tests ===\n");

// --- Branch 1: positive sentiment routes to the thank-you handler ---
await Case("positive routes to thank-you",
    input: "I love this, it's great!",
    expectExecutor: "thank-you",
    expectContains: "Thank you");

// --- Branch 2: negative sentiment routes to escalation ---
await Case("negative routes to escalation",
    input: "This is terrible and broken.",
    expectExecutor: "escalation",
    expectContains: "ESCALATION");

// --- Branch 3: neutral sentiment routes to the default handler ---
await Case("neutral routes to acknowledgement",
    input: "It arrived on Tuesday.",
    expectExecutor: "neutral",
    expectContains: "Acknowledged");

Console.WriteLine();
if (failures == 0)
{
    Console.WriteLine("All workflow branch tests passed.");
    return 0;
}

Console.WriteLine($"{failures} test(s) FAILED.");
return 1;

// ---------------------------------------------------------------
// One test case: run the graph and assert which branch fired and
// what it produced.
// ---------------------------------------------------------------
async Task Case(string name, string input, string expectExecutor, string expectContains)
{
    Workflow workflow = BuildSentimentWorkflow();

    string? output = null;
    HashSet<string> completedExecutors = [];

    StreamingRun run = await InProcessExecution.RunStreamingAsync(
        workflow, input, sessionId: Guid.NewGuid().ToString(), CancellationToken.None);

    await foreach (WorkflowEvent evt in run.WatchStreamAsync(CancellationToken.None))
    {
        switch (evt)
        {
            case ExecutorCompletedEvent completed:
                completedExecutors.Add(completed.ExecutorId);
                break;
            case WorkflowOutputEvent workflowOutput:
                output = workflowOutput.Data?.ToString();
                break;
        }
    }

    bool ranExpectedBranch = completedExecutors.Contains(expectExecutor);
    bool producedExpectedText = output is not null && output.Contains(expectContains, StringComparison.Ordinal);

    Check(name, ranExpectedBranch && producedExpectedText,
        detail: $"branch '{expectExecutor}' ran={ranExpectedBranch}, " +
                $"output contains '{expectContains}'={producedExpectedText} " +
                $"(output: {output})");
}

void Check(string name, bool passed, string detail)
{
    if (passed)
    {
        Console.WriteLine($"  PASS  {name}");
    }
    else
    {
        Console.WriteLine($"  FAIL  {name}  [{detail}]");
        failures++;
    }
}

// ---------------------------------------------------------------
// The graph under test: a keyword sentiment router with three branches.
// ---------------------------------------------------------------
static Workflow BuildSentimentWorkflow()
{
    ExecutorBinding analyzer = new SentimentAnalyzer().BindExecutor();
    ExecutorBinding thankYou = new ResponderExecutor("thank-you", "Thank you for the kind words!").BindExecutor();
    ExecutorBinding escalation = new ResponderExecutor("escalation", "ESCALATION: routing to support.").BindExecutor();
    ExecutorBinding neutral = new ResponderExecutor("neutral", "Acknowledged.").BindExecutor();

    return new WorkflowBuilder(analyzer)
        .AddEdge<Sentiment>(analyzer, thankYou, s => s == Sentiment.Positive)
        .AddEdge<Sentiment>(analyzer, escalation, s => s == Sentiment.Negative)
        .AddEdge<Sentiment>(analyzer, neutral, s => s == Sentiment.Neutral)
        .WithOutputFrom(thankYou, escalation, neutral)
        .Build();
}

internal enum Sentiment { Positive, Negative, Neutral }

/// <summary>Deterministic keyword sentiment classifier.</summary>
internal sealed class SentimentAnalyzer() : Executor<string, Sentiment>("analyzer")
{
    public override ValueTask<Sentiment> HandleAsync(
        string text, IWorkflowContext context, CancellationToken ct)
    {
        string lower = text.ToLowerInvariant();
        Sentiment sentiment =
            lower.Contains("love") || lower.Contains("great") || lower.Contains("excellent")
                ? Sentiment.Positive
            : lower.Contains("terrible") || lower.Contains("broken") || lower.Contains("hate")
                ? Sentiment.Negative
            : Sentiment.Neutral;
        return ValueTask.FromResult(sentiment);
    }
}

/// <summary>A branch handler that yields a fixed response as the workflow output.</summary>
[YieldsOutput(typeof(string))]
internal sealed class ResponderExecutor(string id, string response) : Executor<Sentiment>(id)
{
    private readonly string _response = response;

    public override async ValueTask HandleAsync(
        Sentiment sentiment, IWorkflowContext context, CancellationToken ct)
        => await context.YieldOutputAsync(_response, ct);
}
