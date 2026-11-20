// ============================================================================
// The durable claim-review graph and its version.
//
//   summarize --> [claim-review port] --> decide
//   (--graph next adds fraud-check between summarize and the port)
//
// Every message is a plain record, so it serializes into the checkpoint. The
// summarizer keeps executor state (how many summaries, for which claim
// version) and writes it into the checkpoint; RegisterCheckpointRestoringCallback
// reads it back in the resuming process.
// ============================================================================

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Ch15.DurableReview;

/// <summary>The claim as the business record holds it when the review starts. Version 1, 2, ... per amendment.</summary>
public sealed record ClaimTicket(string ClaimId, int ClaimVersion, string Requester, decimal Amount, string Currency);

/// <summary>What the reviewer is asked about: one claim at one version.</summary>
public sealed record ReviewRequest(string ClaimId, int ClaimVersion, string Requester, decimal Amount, string Currency, string Summary);

public enum ReviewDecision { Approve, Reject }

/// <summary>The reviewer's typed decision, bound to the claim version it covers.</summary>
public sealed record ReviewVerdict(string ClaimId, int ClaimVersion, string ReviewerId, ReviewDecision Decision);

/// <summary>What the workflow yields. Only Status "approved" approves anything.</summary>
public sealed record ReviewOutcome(string ClaimId, int ClaimVersion, string Status, string ReviewerId, string RulesVersion);

public static class ReviewGraph
{
    public const string PortId = "claim-review";

    /// <summary>The rules this build applies. Bump it when a rule changes without changing the graph.</summary>
    public const string RulesVersion = "review-rules-1";
    public const string NextRulesVersion = "review-rules-2";

    /// <summary>
    /// A fresh graph per run. <paramref name="next"/> builds the upgraded graph
    /// (a fraud-check step before the port), to show a pending review meeting a
    /// new binary.
    /// </summary>
    public static Workflow Build(IChatClient chatClient, string rulesVersion = RulesVersion, bool next = false)
    {
        AIAgent writer = new ChatClientAgent(chatClient,
            instructions: "You summarise an expense claim for a finance reviewer in two sentences. " +
                          "State the requester, the amount and the currency. Output only the summary.",
            name: "ClaimSummarizer");

        ExecutorBinding summarize = new SummarizeExecutor(writer).BindExecutor();
        ExecutorBinding port = RequestPort.Create<ReviewRequest, ReviewVerdict>(PortId).BindAsExecutor();
        ExecutorBinding decide = new DecideExecutor(rulesVersion).BindExecutor();

        WorkflowBuilder builder = new(summarize);
        if (next)
        {
            ExecutorBinding fraud = new FraudCheckExecutor().BindExecutor();
            builder.AddEdge(summarize, fraud).AddEdge(fraud, port);
        }
        else
        {
            builder.AddEdge(summarize, port);
        }
        return builder.AddEdge(port, decide)
            .WithOutputFrom(decide)
            .WithName("DurableClaimReview")
            .Build();
    }
}

/// <summary>
/// Writes the reviewer summary. Its executor state (summaries written, last
/// claim and version) goes into every checkpoint and comes back in the
/// resuming process through the restoring callback registered in the constructor.
/// </summary>
internal sealed class SummarizeExecutor : Executor<ClaimTicket, ReviewRequest>
{
    public const string Scope = "review";
    private const string StateKey = "summarizer";
    private readonly AIAgent _writer;
    private SummarizerState _state = new(0, "");

    public SummarizeExecutor(AIAgent writer) : base("summarize")
    {
        _writer = writer;
        RegisterCheckpointRestoringCallback(async (context, ct) =>
        {
            _state = await context.ReadStateAsync<SummarizerState>(StateKey, cancellationToken: ct) ?? new(0, "");
            Console.WriteLine($"  [summarize] restoring callback: {_state.Summaries} summary written, last {_state.LastClaim}");
        });
    }

    public override async ValueTask<ReviewRequest> HandleAsync(ClaimTicket claim, IWorkflowContext context, CancellationToken ct)
    {
        string prompt = $"Claim {claim.ClaimId} (version {claim.ClaimVersion}) from {claim.Requester}: " +
                        $"{claim.Amount:0.00} {claim.Currency}.";
        AgentResponse response = await _writer.RunAsync(prompt, await _writer.CreateSessionAsync(ct), cancellationToken: ct);
        _state = new SummarizerState(_state.Summaries + 1, $"{claim.ClaimId} v{claim.ClaimVersion}");

        ReviewRequest request = new(claim.ClaimId, claim.ClaimVersion, claim.Requester, claim.Amount, claim.Currency,
            response.Text.Trim());
        await context.QueueStateUpdateAsync("request", request, Scope, ct);     // read by DecideExecutor
        return request;
    }

    protected override async ValueTask OnCheckpointingAsync(IWorkflowContext context, CancellationToken ct = default)
        => await context.QueueStateUpdateAsync(StateKey, _state, cancellationToken: ct);

    protected override ValueTask OnCheckpointRestoredAsync(IWorkflowContext context, CancellationToken ct = default)
    {
        Console.WriteLine("  [summarize] OnCheckpointRestoredAsync (runs after the callback)");
        return ValueTask.CompletedTask;
    }
}

public sealed record SummarizerState(int Summaries, string LastClaim);

/// <summary>The upgraded graph's extra step: it passes the request through.</summary>
internal sealed class FraudCheckExecutor() : Executor<ReviewRequest, ReviewRequest>("fraud-check")
{
    public override ValueTask<ReviewRequest> HandleAsync(ReviewRequest request, IWorkflowContext context, CancellationToken ct)
        => ValueTask.FromResult(request);
}

/// <summary>
/// Turns the verdict into the outcome. A verdict for another claim or another
/// version of it approves nothing: the graph checks it against the request it raised.
/// </summary>
[YieldsOutput(typeof(ReviewOutcome))]
internal sealed class DecideExecutor(string rulesVersion) : Executor<ReviewVerdict>("decide")
{
    public override async ValueTask HandleAsync(ReviewVerdict verdict, IWorkflowContext context, CancellationToken ct)
    {
        ReviewRequest? asked = await context.ReadStateAsync<ReviewRequest>("request", SummarizeExecutor.Scope, ct);
        string status = asked is null || asked.ClaimId != verdict.ClaimId || asked.ClaimVersion != verdict.ClaimVersion
            ? "refused_mismatch"
            : verdict.Decision == ReviewDecision.Approve ? "approved" : "rejected";
        await context.YieldOutputAsync(new ReviewOutcome(verdict.ClaimId, verdict.ClaimVersion, status,
            verdict.ReviewerId, rulesVersion), ct);
    }
}
