// ============================================================================
// The Contoso claim-review graph. Program.cs runs it against a model and a
// person at the console; Ch14.WorkflowTests compiles this same file and runs
// every branch with a scripted model and a scripted reviewer.
//
//   classify --switch--> policy-check  \
//            |           receipt-check  >-- barrier --> collect --> write --> [claim-review port]
//            '--default--> returned                                   ^              |
//                                                                     |   switch on ReviewReply.Decision
//                                                      revise (capped)<-- Revise     |
//                                                                    Approve --> approved
//                                                                    Reject  --> rejected
//                                                                    default --> undecided (not-approved)
//
// No reply means no outcome: the run stays at PendingRequests, nothing is approved.
// ============================================================================

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Ch14.ApprovalWorkflow;

public sealed record ExpenseClaim(string Id, string Category, decimal Amount, bool HasReceipt, string Description);
public enum ClaimRoute { Review, Unsupported }
public sealed record RoutedClaim(ExpenseClaim Claim, ClaimRoute Route);
public sealed record CheckResult(ExpenseClaim Claim, string Check, bool Passed, string Detail);
public sealed record DraftRequest(ExpenseClaim Claim, IReadOnlyList<CheckResult> Checks, string Note, int Revision);
public sealed record ReviewRequest(ExpenseClaim Claim, IReadOnlyList<CheckResult> Checks, string Summary, int Revision);

/// <summary>The only three things a reviewer can decide. None of them is 0, so default(ReviewDecision) is no decision.</summary>
public enum ReviewDecision { Approve = 1, Revise = 2, Reject = 3 }
public sealed record ReviewReply(ReviewDecision Decision, string Note = "");

/// <summary>What the workflow yields. Only Status "approved" means approved.</summary>
public sealed record ClaimOutcome(string ClaimId, string Status, string Detail);

public static class ClaimReviewGraph
{
    public const string PortId = "claim-review";

    /// <summary>
    /// Builds a fresh graph: new executor instances every call, so no state is
    /// shared between runs. Build one per run.
    /// </summary>
    public static Workflow Build(IChatClient chatClient, int maxRevisions = 2)
    {
        AIAgent writerAgent = new ChatClientAgent(chatClient,
            instructions: "You summarise an expense claim for a human reviewer in two or three " +
                          "sentences. State the amount, the category and every failed check. " +
                          "If a reviewer note is given, address it. Output only the summary.",
            name: "ClaimWriter");

        ExecutorBinding classify = new ClassifyExecutor().BindExecutor();
        ExecutorBinding policy = new PolicyCheckExecutor().BindExecutor();
        ExecutorBinding receipt = new ReceiptCheckExecutor().BindExecutor();
        ExecutorBinding returned = new ReturnedExecutor().BindExecutor();
        ExecutorBinding collect = new CollectChecksExecutor(expected: 2).BindExecutor();
        ExecutorBinding write = new WriteSummaryExecutor(writerAgent).BindExecutor();
        ExecutorBinding port = RequestPort.Create<ReviewRequest, ReviewReply>(PortId).BindAsExecutor();
        ExecutorBinding approved = new ApprovedExecutor().BindExecutor();
        ExecutorBinding revise = new ReviseExecutor(maxRevisions).BindExecutor();
        ExecutorBinding rejected = new RejectedExecutor().BindExecutor();
        ExecutorBinding undecided = new UndecidedExecutor().BindExecutor();

        return new WorkflowBuilder(classify)
            .AddSwitch(classify, sw => sw
                .AddCase<RoutedClaim>(c => c is { Route: ClaimRoute.Review }, [policy, receipt])
                .WithDefault([returned]))
            .AddFanInBarrierEdge([policy, receipt], collect)
            .AddEdge(collect, write)
            .AddEdge(write, port)
            .AddSwitch(port, sw => sw
                .AddCase<ReviewReply>(r => r is { Decision: ReviewDecision.Approve }, [approved])
                .AddCase<ReviewReply>(r => r is { Decision: ReviewDecision.Revise }, [revise])
                .AddCase<ReviewReply>(r => r is { Decision: ReviewDecision.Reject }, [rejected])
                .WithDefault([undecided]))
            .AddEdge(revise, write)
            .WithOutputFrom(returned, approved, revise, rejected, undecided)
            .WithName("ContosoClaimReview")
            .Build();
    }
}

public static class ReviewDecisions
{
    /// <summary>
    /// Accepts exactly "approve", "revise" or "reject" (case and surrounding spaces
    /// ignored). Empty input, typos, "approved", "yes" and end of input are not decisions.
    /// </summary>
    public static bool TryParse(string? input, out ReviewDecision decision)
    {
        switch (input?.Trim().ToLowerInvariant())
        {
            case "approve": decision = ReviewDecision.Approve; return true;
            case "revise": decision = ReviewDecision.Revise; return true;
            case "reject": decision = ReviewDecision.Reject; return true;
            default: decision = default; return false;
        }
    }
}

// ---------------------------------------------------------------
// Executors. Deterministic ones first; only the writer calls a model.
// ---------------------------------------------------------------

internal sealed class ClassifyExecutor() : Executor<ExpenseClaim, RoutedClaim>("classify")
{
    private static readonly string[] Covered = ["travel", "lodging", "meals"];

    public override ValueTask<RoutedClaim> HandleAsync(ExpenseClaim claim, IWorkflowContext context, CancellationToken ct)
        => ValueTask.FromResult(new RoutedClaim(claim,
            Covered.Contains(claim.Category.ToLowerInvariant()) ? ClaimRoute.Review : ClaimRoute.Unsupported));
}

internal sealed class PolicyCheckExecutor() : Executor<RoutedClaim, CheckResult>("policy-check")
{
    public override ValueTask<CheckResult> HandleAsync(RoutedClaim routed, IWorkflowContext context, CancellationToken ct)
    {
        decimal limit = routed.Claim.Category.ToLowerInvariant() switch { "meals" => 75m, "lodging" => 300m, _ => 2000m };
        bool ok = routed.Claim.Amount <= limit;
        return ValueTask.FromResult(new CheckResult(routed.Claim, "policy", ok,
            ok ? $"within the {limit:0} limit" : $"over the {limit:0} limit"));
    }
}

internal sealed class ReceiptCheckExecutor() : Executor<RoutedClaim, CheckResult>("receipt-check")
{
    public override ValueTask<CheckResult> HandleAsync(RoutedClaim routed, IWorkflowContext context, CancellationToken ct)
    {
        bool ok = routed.Claim.HasReceipt || routed.Claim.Amount < 25m;
        return ValueTask.FromResult(new CheckResult(routed.Claim, "receipt", ok,
            ok ? "receipt present or not required" : "receipt missing"));
    }
}

[YieldsOutput(typeof(ClaimOutcome))]
internal sealed class ReturnedExecutor() : Executor<RoutedClaim>("returned")
{
    public override ValueTask HandleAsync(RoutedClaim routed, IWorkflowContext context, CancellationToken ct)
        => context.YieldOutputAsync(new ClaimOutcome(routed.Claim.Id, "returned",
            $"category '{routed.Claim.Category}' is not covered"), ct);
}

/// <summary>
/// The fan-in barrier delivers one message per branch, so this executor counts.
/// Its list is per-run state: IResettableExecutor clears it if the graph is reused.
/// </summary>
[SendsMessage(typeof(DraftRequest))]
internal sealed class CollectChecksExecutor(int expected) : Executor<CheckResult>("collect"), IResettableExecutor
{
    private readonly List<CheckResult> _checks = [];

    public override async ValueTask HandleAsync(CheckResult check, IWorkflowContext context, CancellationToken ct)
    {
        _checks.Add(check);
        if (_checks.Count < expected) return;
        CheckResult[] all = [.. _checks.OrderBy(c => c.Check)];
        await context.SendMessageAsync(new DraftRequest(check.Claim, all, Note: "", Revision: 0), cancellationToken: ct);
    }

    public ValueTask ResetAsync() { _checks.Clear(); return ValueTask.CompletedTask; }
}

/// <summary>The agent executor: writes the reviewer summary and remembers the request.</summary>
internal sealed class WriteSummaryExecutor(AIAgent agent) : Executor<DraftRequest, ReviewRequest>("write")
{
    public const string Scope = "claim";

    public override async ValueTask<ReviewRequest> HandleAsync(DraftRequest draft, IWorkflowContext context, CancellationToken ct)
    {
        string checks = string.Join("; ", draft.Checks.Select(c => $"{c.Check}: {(c.Passed ? "pass" : "FAIL")} ({c.Detail})"));
        string prompt = $"Claim {draft.Claim.Id}: {draft.Claim.Category}, {draft.Claim.Amount:0.00}. " +
                        $"{draft.Claim.Description}\nChecks: {checks}" +
                        (draft.Note.Length > 0 ? $"\nReviewer note: {draft.Note}" : "");
        AgentResponse response = await agent.RunAsync(prompt, await agent.CreateSessionAsync(ct), cancellationToken: ct);

        ReviewRequest request = new(draft.Claim, draft.Checks, response.Text.Trim(), draft.Revision);
        await context.QueueStateUpdateAsync("last", request, Scope, ct);   // read by ReviseExecutor
        return request;
    }
}

[YieldsOutput(typeof(ClaimOutcome))]
internal sealed class ApprovedExecutor() : Executor<ReviewReply>("approved")
{
    public override async ValueTask HandleAsync(ReviewReply reply, IWorkflowContext context, CancellationToken ct)
    {
        ReviewRequest last = await context.ReadStateAsync<ReviewRequest>("last", WriteSummaryExecutor.Scope, ct)
            ?? throw new InvalidOperationException("approve without a review request: nothing to approve");
        await context.YieldOutputAsync(new ClaimOutcome(last.Claim.Id, "approved", last.Summary), ct);
    }
}

/// <summary>
/// The decision switch's default: a reply whose Decision is none of the three
/// (default(ReviewDecision), a JSON body with no Decision, an out-of-range value).
/// It ends the review visibly as not approved instead of leaving the run idle.
/// </summary>
[YieldsOutput(typeof(ClaimOutcome))]
internal sealed class UndecidedExecutor() : Executor<ReviewReply>("undecided")
{
    public override async ValueTask HandleAsync(ReviewReply reply, IWorkflowContext context, CancellationToken ct)
    {
        ReviewRequest? last = await context.ReadStateAsync<ReviewRequest>("last", WriteSummaryExecutor.Scope, ct);
        await context.YieldOutputAsync(new ClaimOutcome(last?.Claim.Id ?? "?", "not-approved",
            $"decision {(int)reply.Decision} is not approve, revise or reject"), ct);
    }
}

[YieldsOutput(typeof(ClaimOutcome))]
internal sealed class RejectedExecutor() : Executor<ReviewReply>("rejected")
{
    public override async ValueTask HandleAsync(ReviewReply reply, IWorkflowContext context, CancellationToken ct)
    {
        ReviewRequest? last = await context.ReadStateAsync<ReviewRequest>("last", WriteSummaryExecutor.Scope, ct);
        await context.YieldOutputAsync(new ClaimOutcome(last?.Claim.Id ?? "?", "rejected", reply.Note), ct);
    }
}

/// <summary>
/// Sends the claim back to the writer with the reviewer's note, at most
/// <paramref name="maxRevisions"/> times. Hitting the cap ends the review as
/// "not-approved": a loop that stops on its cap has not succeeded.
/// </summary>
[SendsMessage(typeof(DraftRequest))]
[YieldsOutput(typeof(ClaimOutcome))]
internal sealed class ReviseExecutor(int maxRevisions) : Executor<ReviewReply>("revise")
{
    public override async ValueTask HandleAsync(ReviewReply reply, IWorkflowContext context, CancellationToken ct)
    {
        ReviewRequest last = await context.ReadStateAsync<ReviewRequest>("last", WriteSummaryExecutor.Scope, ct)
            ?? throw new InvalidOperationException("revise without a review request");
        if (last.Revision >= maxRevisions)
        {
            await context.YieldOutputAsync(new ClaimOutcome(last.Claim.Id, "not-approved",
                $"revision cap ({maxRevisions}) reached without an approval"), ct);
            return;
        }
        await context.SendMessageAsync(new DraftRequest(last.Claim, last.Checks, reply.Note, last.Revision + 1), cancellationToken: ct);
    }
}
