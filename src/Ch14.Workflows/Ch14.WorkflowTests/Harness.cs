using Ch14.ApprovalWorkflow;
using Microsoft.Agents.AI;
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

internal sealed record ReviewRun(ClaimOutcome? Outcome, RunStatus Status, List<ReviewRequest> Requests, List<string> Errors,
    Dictionary<string, int> Steps);

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
        Dictionary<string, int> steps = [];                             // executor id -> first superstep
        int superstep = 0;
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
                    case ExecutorInvokedEvent invoked:
                        steps.TryAdd(invoked.ExecutorId, superstep);
                        break;
                    case SuperStepCompletedEvent:
                        superstep++;
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
        return new ReviewRun(outcome, await run.GetStatusAsync(), requests, errors, steps);
    }

    /// <summary>
    /// Answers the typed port with a string, then, on the same run, with a typed
    /// approval. Returns the refusal, the run's status after it, and the outcome
    /// of the typed reply.
    /// </summary>
    public static async Task<(string? Refusal, RunStatus After, ClaimOutcome? Typed)> SendWrongTypeAsync(ExpenseClaim claim)
    {
        await using StreamingRun run = await InProcessExecution.RunStreamingAsync(
            ClaimReviewGraph.Build(new ScriptedChatClient()), claim, sessionId: Guid.NewGuid().ToString());
        ExternalRequest? asked = await FirstRequestAsync(run);
        string? refusal = null;
        try { await run.SendResponseAsync(asked!.CreateResponse("approve")); }
        catch (InvalidOperationException ex) { refusal = ex.Message; }
        RunStatus after = await run.GetStatusAsync();

        await run.SendResponseAsync(asked!.CreateResponse(new ReviewReply(ReviewDecision.Approve)));
        ClaimOutcome? typed = null;
        await foreach (WorkflowEvent evt in run.WatchStreamAsync(blockOnPendingRequest: false))
            if (evt is WorkflowOutputEvent { Data: ClaimOutcome done }) typed = done;
        return (refusal, after, typed);
    }

    /// <summary>Sends the same approval twice; returns the workflow error, the final status and any outcome.</summary>
    public static async Task<(string? Error, RunStatus Status, ClaimOutcome? Outcome)> SendTwiceAsync(ExpenseClaim claim)
    {
        await using StreamingRun run = await InProcessExecution.RunStreamingAsync(
            ClaimReviewGraph.Build(new ScriptedChatClient()), claim, sessionId: Guid.NewGuid().ToString());
        ExternalRequest? asked = await FirstRequestAsync(run);
        ExternalResponse reply = asked!.CreateResponse(new ReviewReply(ReviewDecision.Approve));
        await run.SendResponseAsync(reply);
        await run.SendResponseAsync(reply);
        string? error = null;
        ClaimOutcome? outcome = null;
        await foreach (WorkflowEvent evt in run.WatchStreamAsync(blockOnPendingRequest: false))
        {
            if (evt is WorkflowErrorEvent failure) error ??= failure.Data?.ToString();
            if (evt is WorkflowOutputEvent { Data: ClaimOutcome done }) outcome = done;
        }
        return (error, await run.GetStatusAsync(), outcome);
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
    public static async Task<string?> SecondRunWhileOwnedAsync(ExpenseClaim claim)
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

    /// <summary>
    /// Two runs of one graph started at the same moment: both threads wait at a
    /// barrier, then call RunStreamingAsync together. Repeated <paramref name="rounds"/>
    /// times; returns how many rounds ended with exactly one run started and one refused.
    /// </summary>
    public static async Task<int> TwoRunsAtOnceAsync(ExpenseClaim claim, int rounds)
    {
        int exactlyOne = 0;
        for (int i = 0; i < rounds; i++)
        {
            Workflow shared = ClaimReviewGraph.Build(new ScriptedChatClient());
            using Barrier gate = new(2);
            async Task<StreamingRun?> StartAsync(string id)
            {
                gate.SignalAndWait();
                try { return await InProcessExecution.RunStreamingAsync(shared, claim, sessionId: id); }
                catch (InvalidOperationException) { return null; }
            }
            StreamingRun?[] runs = await Task.WhenAll(Task.Run(() => StartAsync("a")), Task.Run(() => StartAsync("b")));
            if (runs.Count(r => r is not null) == 1) exactlyOne++;
            foreach (StreamingRun? run in runs)
                if (run is not null) await run.DisposeAsync();
        }
        return exactlyOne;
    }
}

/// <summary>
/// Counts how often switch predicates are called with null: behind a RequestPort,
/// and behind an ordinary executor. Then runs a port switch whose predicate is not
/// null-safe.
/// </summary>
internal static class NullProbe
{
    public static async Task<(int PortNulls, int PlainNulls, ClaimOutcome? UnsafeOutcome, List<string> UnsafeErrors)> RunAsync(ExpenseClaim claim)
    {
        int portNulls = 0, plainNulls = 0;

        // 1. The claim graph's shape: a switch after the review port, null-safe but counting.
        await DriveAsync(PortGraph(r => { if (r is null) portNulls++; return r is { Decision: ReviewDecision.Approve }; }), claim);

        // 2. A switch after an ordinary executor (classify), counting.
        ExecutorBinding classify = new ClassifyExecutor().BindExecutor();
        ExecutorBinding returned = new ReturnedExecutor().BindExecutor();
        Workflow plain = new WorkflowBuilder(classify)
            .AddSwitch(classify, sw => sw
                .AddCase<RoutedClaim>(c => { if (c is null) plainNulls++; return c is { Route: ClaimRoute.Unsupported }; }, [returned]))
            .WithOutputFrom(returned)
            .Build();
        await DriveAsync(plain, claim with { Category = "gym" });

        // 3. The same port switch written without a null check.
        (ClaimOutcome? outcome, List<string> errors) = await DriveAsync(PortGraph(r => r!.Decision == ReviewDecision.Approve), claim);
        return (portNulls, plainNulls, outcome, errors);
    }

    private static Workflow PortGraph(Func<ReviewReply?, bool> approve)
    {
        ExecutorBinding classify = new ClassifyExecutor().BindExecutor();
        ExecutorBinding policy = new PolicyCheckExecutor().BindExecutor();
        ExecutorBinding receipt = new ReceiptCheckExecutor().BindExecutor();
        ExecutorBinding collect = new CollectChecksExecutor(expected: 2).BindExecutor();
        ExecutorBinding write = new WriteSummaryExecutor(
            new ChatClientAgent(new ScriptedChatClient(), instructions: "Summarise the claim.")).BindExecutor();
        ExecutorBinding port = RequestPort.Create<ReviewRequest, ReviewReply>(ClaimReviewGraph.PortId).BindAsExecutor();
        ExecutorBinding approved = new ApprovedExecutor().BindExecutor();
        return new WorkflowBuilder(classify)
            .AddFanOutEdge(classify, [policy, receipt])
            .AddFanInBarrierEdge([policy, receipt], collect)
            .AddEdge(collect, write)
            .AddEdge(write, port)
            .AddSwitch(port, sw => sw.AddCase<ReviewReply>(r => approve(r), [approved]))
            .WithOutputFrom(approved)
            .Build();
    }

    private static async Task<(ClaimOutcome?, List<string>)> DriveAsync(Workflow workflow, ExpenseClaim claim)
    {
        await using StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow, claim, sessionId: Guid.NewGuid().ToString());
        ClaimOutcome? outcome = null;
        List<string> errors = [];
        for (int round = 0; round < 3; round++)
        {
            ExternalRequest? asked = null;
            await foreach (WorkflowEvent evt in run.WatchStreamAsync(blockOnPendingRequest: false))
            {
                if (evt is RequestInfoEvent info) asked ??= info.Request;
                if (evt is WorkflowOutputEvent { Data: ClaimOutcome done }) outcome = done;
                if (evt is WorkflowErrorEvent error) errors.Add(error.Data?.ToString() ?? "error");
            }
            if (asked is null) break;
            await run.SendResponseAsync(asked.CreateResponse(new ReviewReply(ReviewDecision.Approve)));
        }
        return (outcome, errors);
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
