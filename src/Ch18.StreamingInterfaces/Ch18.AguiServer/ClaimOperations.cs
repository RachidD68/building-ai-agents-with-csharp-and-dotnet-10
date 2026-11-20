using System.Collections.Concurrent;
using System.ComponentModel;
using Ch08.ActionPolicy;
using Ch18.ApprovalUi;
using Ch18.SseStatusStream;
using Microsoft.Extensions.AI;

namespace Ch18.AguiServer;

/// <summary>
/// The application behind the agent's tools. The agent instance is built once
/// (MapAGUI resolves it when the route is mapped, whatever the lifetime), so
/// nothing here holds an identity: every tool call reads the caller from the
/// current request. The model proposes; this class decides the operation's
/// state and writes it to the status stream of the AG-UI thread.
/// </summary>
public sealed class ClaimOperations(IHttpContextAccessor accessor, ApprovalDesk desk, OperationLog log, Rights rights)
{
    private readonly ConcurrentDictionary<Guid, Claim> _claims = new();

    public sealed record Claim(Guid ProposalId, string Tenant, string Requester, decimal Amount, string Currency,
        string Thread, string RunId);

    public IEnumerable<AITool> EmployeeTools() =>
    [
        AIFunctionFactory.Create(SubmitClaim, "submit_claim"),
        AIFunctionFactory.Create(ListMyClaims, "list_my_claims"),
    ];

    public IEnumerable<AITool> ReviewerTools() => [AIFunctionFactory.Create(PendingReviews, "pending_reviews")];

    [Description("Submits an expense claim for the signed-in employee. Returns the claim's state.")]
    private string SubmitClaim([Description("Amount")] decimal amount, [Description("ISO currency code, e.g. EUR")] string currency)
    {
        (CallerKey who, string thread) = Current();
        if (desk.Commits.LoadAccount(who.TenantId, who.SubjectId) is not { } account) return "refused: no expense account";
        string run = log.StartRun(thread);
        ReimbursementProposal args = new(Guid.NewGuid(), who.SubjectId, amount, currency, account.Version);
        Guid id = desk.Propose(who.TenantId, who.SubjectId, args);
        _claims[id] = new Claim(id, who.TenantId, who.SubjectId, amount, currency, thread, run);
        if (amount <= ReimbursementPolicy.LowValueLimit) return Finish(id, "low_value");
        log.SetState(thread, run, OperationState.AwaitingApproval, "amount_above_review_limit");
        return $"claim {id:D} for {amount} {currency}: waiting for a finance reviewer";
    }

    [Description("Lists the signed-in employee's claims.")]
    private string ListMyClaims()
    {
        (CallerKey who, _) = Current();
        Claim[] mine = [.. _claims.Values.Where(c => c.Tenant == who.TenantId && c.Requester == who.SubjectId)];
        return mine.Length == 0 ? "no claims" : string.Join("; ", mine.Select(c => $"{c.Amount} {c.Currency} ({c.ProposalId:D})"));
    }

    [Description("Lists claims waiting for the signed-in reviewer.")]
    private string PendingReviews()
    {
        (CallerKey who, _) = Current();
        string[] pending = [.. _claims.Values
            .Where(c => c.Tenant == who.TenantId && c.Requester != who.SubjectId
                && desk.View(who.TenantId, who.SubjectId, c.ProposalId)?.Status == nameof(ProposalStatus.Pending))
            .Select(c => $"{c.Requester}: {c.Amount} {c.Currency} ({c.ProposalId:D})")];
        return pending.Length == 0 ? "nothing to review" : string.Join("; ", pending);
    }

    /// <summary>After a stored decision: commit an approval, end a rejection, keep a revision pending.</summary>
    public void OnDecided(Guid proposalId, DecisionResult result)
    {
        if (!_claims.TryGetValue(proposalId, out Claim? c)) return;
        switch (result.Result)
        {
            case nameof(ProposalStatus.Approved): Finish(proposalId, "approved"); break;
            case nameof(ProposalStatus.Rejected): log.SetState(c.Thread, c.RunId, OperationState.Failed, "rejected_by_reviewer"); break;
            default: log.SetState(c.Thread, c.RunId, OperationState.AwaitingApproval, "revision_requested"); break;
        }
    }

    public Claim? Find(Guid proposalId) => _claims.GetValueOrDefault(proposalId);

    /// <summary>The proposals waiting for a decision (the reviewer's queue).</summary>
    public IEnumerable<Guid> Pending() => _claims.Keys.Where(id =>
        log.Snapshot(_claims[id].Thread) is { State: OperationState.AwaitingApproval } s && s.RunId == _claims[id].RunId);

    // Completed only after the commit this process observed; anything else is not done.
    private string Finish(Guid id, string why)
    {
        Claim c = _claims[id];
        (PolicyResult policy, Ch08.IdempotentCommit.CommitResult? commit) =
            desk.Commit(id, new Ch08.ActionPolicy.Caller(c.Tenant, c.Requester, rights.Of(c.Tenant, c.Requester)));
        if (commit?.Status is Ch08.IdempotentCommit.CommitStatus.Committed or Ch08.IdempotentCommit.CommitStatus.Replayed)
        {
            log.SetState(c.Thread, c.RunId, OperationState.Completed, $"committed_{why}");
            return $"claim {id:D}: committed";
        }
        log.SetState(c.Thread, c.RunId, OperationState.Failed, commit is null ? policy.Reason : $"commit_{commit.Status}");
        return $"claim {id:D}: refused ({policy.Reason})";
    }

    private (CallerKey Who, string Thread) Current()
    {
        HttpContext http = accessor.HttpContext ?? throw new InvalidOperationException("a tool ran outside a request");
        CallerKey who = CallerKey.From(http.User) ?? throw new InvalidOperationException("no identity key");
        return (who, http.Items[AguiHost.ThreadItem] as string ?? throw new InvalidOperationException("no AG-UI thread"));
    }
}
