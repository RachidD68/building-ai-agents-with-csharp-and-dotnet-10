using Ch14.ApprovalWorkflow;
using Microsoft.Agents.AI.Workflows;

namespace Ch14.WorkflowTests;

internal static class Harness
{
    private static int s_passed;
    private static int s_total;

    public static void Check(string name, bool ok, string? detail = null)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(ok || detail is null ? "" : $"  [{detail}]")}");
    }

    /// <summary>Passes when the broken behaviour really happens.</summary>
    public static void Defect(string name, bool reproduced)
    {
        s_total++;
        if (reproduced) s_passed++;
        Console.WriteLine($"  {(reproduced ? "DEFECT REPRODUCED" : "FAIL (defect not reproduced)")}  {name}");
    }

    public static int Finish()
    {
        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }
}

internal sealed record ReviewRun(ClaimOutcome? Outcome, RunStatus Status, List<ReviewRequest> Requests, List<string> Errors);

internal static class Review
{
    /// <summary>
    /// Runs one claim through a fresh claim-review graph. The scripted reviewer
    /// returns a reply, or null to send nothing (the review stays pending).
    /// </summary>
    public static async Task<ReviewRun> RunAsync(ExpenseClaim claim, Func<ReviewRequest, ReviewReply?> reviewer,
        int maxRevisions = 2, int maxRounds = 20)
    {
        Workflow workflow = ClaimReviewGraph.Build(new ScriptedChatClient(), maxRevisions);
        await using StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow, claim, sessionId: Guid.NewGuid().ToString());

        List<ReviewRequest> requests = [];
        List<string> errors = [];
        ClaimOutcome? outcome = null;
        for (int round = 0; round < maxRounds; round++)
        {
            ExternalRequest? asked = null;
            await foreach (WorkflowEvent evt in run.WatchStreamAsync(blockOnPendingRequest: false))
            {
                switch (evt)
                {
                    case RequestInfoEvent info when info.Request.TryGetDataAs(out ReviewRequest? request):
                        asked = info.Request;
                        requests.Add(request);
                        break;
                    case WorkflowOutputEvent { Data: ClaimOutcome done }:
                        outcome = done;
                        break;
                    case WorkflowErrorEvent error:
                        errors.Add(error.Data?.ToString() ?? "error");
                        break;
                }
            }
            if (asked is null) break;
            ReviewReply? reply = reviewer(requests[^1]);
            if (reply is null) break;                                   // nobody decided
            await run.SendResponseAsync(asked.CreateResponse(reply));
        }
        return new ReviewRun(outcome, await run.GetStatusAsync(), requests, errors);
    }

    /// <summary>Answers the typed port with a string; returns the refusal message.</summary>
    public static async Task<string?> SendWrongTypeAsync(ExpenseClaim claim)
    {
        await using StreamingRun run = await InProcessExecution.RunStreamingAsync(
            ClaimReviewGraph.Build(new ScriptedChatClient()), claim, sessionId: Guid.NewGuid().ToString());
        ExternalRequest? asked = await FirstRequestAsync(run);
        try { await run.SendResponseAsync(asked!.CreateResponse("approve")); return null; }
        catch (InvalidOperationException ex) { return ex.Message; }
    }

    /// <summary>Sends the same approval twice; returns the workflow error it causes.</summary>
    public static async Task<string?> SendTwiceAsync(ExpenseClaim claim)
    {
        await using StreamingRun run = await InProcessExecution.RunStreamingAsync(
            ClaimReviewGraph.Build(new ScriptedChatClient()), claim, sessionId: Guid.NewGuid().ToString());
        ExternalRequest? asked = await FirstRequestAsync(run);
        ExternalResponse reply = asked!.CreateResponse(new ReviewReply(ReviewDecision.Approve));
        await run.SendResponseAsync(reply);
        await run.SendResponseAsync(reply);
        string? error = null;
        await foreach (WorkflowEvent evt in run.WatchStreamAsync(blockOnPendingRequest: false))
            if (evt is WorkflowErrorEvent failure) error ??= failure.Data?.ToString();
        return error;
    }

    public static async Task<ExternalRequest?> FirstRequestAsync(StreamingRun run)
    {
        ExternalRequest? asked = null;
        await foreach (WorkflowEvent evt in run.WatchStreamAsync(blockOnPendingRequest: false))
            if (evt is RequestInfoEvent info) asked ??= info.Request;
        return asked;
    }

    /// <summary>The first edition's rule, kept only to reproduce it.</summary>
    public static string V1Rule(string? line)
    {
        string? decision = line?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(decision) || (decision != "approve" && decision != "revise"))
            decision = "approve"; // Default to approve.
        return decision;
    }
}

internal static class Reuse
{
    public static async Task<(string?, string?)> SharedCounterAsync()
    {
        Workflow shared = Counter.Graph(new Counter());
        return (await Counter.RunAsync(shared), await Counter.RunAsync(shared));
    }

    public static async Task<(string?, string?)> FreshCounterAsync()
        => (await Counter.RunAsync(Counter.Graph(new Counter())), await Counter.RunAsync(Counter.Graph(new Counter())));

    public static async Task<(string?, string?)> ResettableCounterAsync()
    {
        Workflow shared = Counter.Graph(new ResettableCounter());
        return (await Counter.RunAsync(shared), await Counter.RunAsync(shared));
    }

    /// <summary>
    /// Run 1 pauses at the review port and keeps the graph; run 2 starts on
    /// another thread while run 1 is still open. Returns run 2's refusal.
    /// </summary>
    public static async Task<string?> ConcurrentRunAsync(ExpenseClaim claim)
    {
        Workflow shared = ClaimReviewGraph.Build(new ScriptedChatClient());
        await using StreamingRun first = await InProcessExecution.RunStreamingAsync(shared, claim, sessionId: "run-1");
        await Review.FirstRequestAsync(first);
        return await Task.Run(async () =>
        {
            try
            {
                await using StreamingRun second = await InProcessExecution.RunStreamingAsync(shared, claim, sessionId: "run-2");
                return null;
            }
            catch (InvalidOperationException ex) { return ex.Message; }
        });
    }
}

/// <summary>Counts the messages it has seen: per-run state kept in a field.</summary>
[YieldsOutput(typeof(string))]
internal class Counter() : Executor<string>("counter")
{
    protected int Seen;

    public override async ValueTask HandleAsync(string message, IWorkflowContext context, CancellationToken ct)
        => await context.YieldOutputAsync($"seen {++Seen}", ct);

    public static Workflow Graph(Counter counter)
    {
        ExecutorBinding node = counter.BindExecutor();
        return new WorkflowBuilder(node).WithOutputFrom(node).Build();
    }

    public static async Task<string?> RunAsync(Workflow workflow)
    {
        await using StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow, "claim", sessionId: Guid.NewGuid().ToString());
        string? output = null;
        await foreach (WorkflowEvent evt in run.WatchStreamAsync(blockOnPendingRequest: false))
            if (evt is WorkflowOutputEvent o) output = o.Data?.ToString();
        return output;
    }
}

/// <summary>The same counter, reset by the runtime before a graph is reused.</summary>
internal sealed class ResettableCounter : Counter, IResettableExecutor
{
    public ValueTask ResetAsync() { Seen = 0; return ValueTask.CompletedTask; }
}

internal static class Sentiment
{
    public static async Task CaseAsync(string name, string input, string expectExecutor, string expectContains)
    {
        ExecutorBinding analyzer = new SentimentAnalyzer().BindExecutor();
        ExecutorBinding thankYou = new ResponderExecutor("thank-you", "Thank you for the kind words!").BindExecutor();
        ExecutorBinding escalation = new ResponderExecutor("escalation", "ESCALATION: routing to support.").BindExecutor();
        ExecutorBinding neutral = new ResponderExecutor("neutral", "Acknowledged.").BindExecutor();
        Workflow workflow = new WorkflowBuilder(analyzer)
            .AddEdge<SentimentKind>(analyzer, thankYou, s => s == SentimentKind.Positive)
            .AddEdge<SentimentKind>(analyzer, escalation, s => s == SentimentKind.Negative)
            .AddEdge<SentimentKind>(analyzer, neutral, s => s == SentimentKind.Neutral)
            .WithOutputFrom(thankYou, escalation, neutral)
            .Build();

        string? output = null;
        HashSet<string> completed = [];
        await using StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow, input, sessionId: Guid.NewGuid().ToString());
        await foreach (WorkflowEvent evt in run.WatchStreamAsync(blockOnPendingRequest: false))
        {
            if (evt is ExecutorCompletedEvent done) completed.Add(done.ExecutorId);
            if (evt is WorkflowOutputEvent o) output = o.Data?.ToString();
        }
        Harness.Check(name, completed.Contains(expectExecutor) && output?.Contains(expectContains, StringComparison.Ordinal) == true,
            $"ran={string.Join(",", completed)} output={output}");
    }
}

internal enum SentimentKind { Positive, Negative, Neutral }

/// <summary>Deterministic keyword sentiment classifier.</summary>
internal sealed class SentimentAnalyzer() : Executor<string, SentimentKind>("analyzer")
{
    public override ValueTask<SentimentKind> HandleAsync(string text, IWorkflowContext context, CancellationToken ct)
    {
        string lower = text.ToLowerInvariant();
        SentimentKind kind =
            lower.Contains("love") || lower.Contains("great") || lower.Contains("excellent") ? SentimentKind.Positive
            : lower.Contains("terrible") || lower.Contains("broken") || lower.Contains("hate") ? SentimentKind.Negative
            : SentimentKind.Neutral;
        return ValueTask.FromResult(kind);
    }
}

/// <summary>A branch handler that yields a fixed response as the workflow output.</summary>
[YieldsOutput(typeof(string))]
internal sealed class ResponderExecutor(string id, string response) : Executor<SentimentKind>(id)
{
    public override async ValueTask HandleAsync(SentimentKind sentiment, IWorkflowContext context, CancellationToken ct)
        => await context.YieldOutputAsync(response, ct);
}
