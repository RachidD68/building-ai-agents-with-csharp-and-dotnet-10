using System.Collections.Concurrent;
using Ch08.ActionPolicy;
using Ch08.UncertainOutcome;
using Ch18.SseStatusStream;
using Contoso.AuthorizedReimbursement;
using Contoso.DurableResume;
using Contoso.Receipts;

namespace Contoso.ApiUi;

/// <summary>
/// The interface half of v6 (the interface chapter), over the same v5 store:
///   GET  /claims/{id}/events     the claim's operation state as typed SSE, resumable (Ch18.SseStatusStream)
///   GET  /claims/{id}/snapshot   the same view at the current cursor
///   GET  /claims/{id}/review     the exact arguments a reviewer approves: amount, currency,
///                                account version and their digest (ch. 8's ReimbursementPolicy.Digest)
///   POST /claims/{id}/approval   {"accountVersion":7,"digest":"...","decision":"approve"|"reject"}
/// Who may read or decide is ClaimsApi's rule (requester or finance reviewer of the tenant, else 404).
/// The state comes from v5's store, never from text: v5's worker still pays.
/// </summary>
public static class InterfaceApi
{
    public static WebApplication Map(WebApplication app, string store)
    {
        string appDb = Path.Combine(store, "app.db"), journal = Path.Combine(store, "provider.db");
        ClaimFeed feed = app.Services.GetRequiredService<ClaimFeed>();
        feed.Store = (appDb, journal);

        app.MapStatusStream("/claims", feed.Log, (http, id) =>
            ClaimsApi.Open(http, appDb, journal, id) is { } c && feed.Track(c)).RequireAuthorization().RequireRateLimiting("per-caller");

        app.MapGet("/claims/{id}/review", (string id, HttpContext http) =>
            ClaimsApi.Open(http, appDb, journal, id) is { } c && c.Caller.Permissions.Contains(Roles.FinanceReviewer)
                && Review(c) is { } review
                ? Results.Ok(review)
                : Results.NotFound()).RequireAuthorization().RequireRateLimiting("per-caller");

        app.MapPost("/claims/{id}/approval", (string id, ApprovalRequest request, HttpContext http) =>
        {
            if (ClaimsApi.Open(http, appDb, journal, id) is not { } c) return Results.NotFound();
            (int status, string result) = Approve(c, request);
            if (status == 200) feed.Track(c);
            return Results.Json(new { result }, statusCode: status);
        }).RequireAuthorization().RequireRateLimiting("per-caller");
        return app;
    }

    /// <summary>What the reviewer approves, computed by the server from v5's claim of record.</summary>
    internal static ClaimReview? Review(ClaimsApi.Context c)
    {
        Guid op = c.Session.OperationId;
        if (c.Desk.AmountOfRecord(DurableClaims.Tenant, op) is not { } amount
            || c.Desk.Store.LoadAccount(DurableClaims.Tenant, c.Session.Requester) is not { } account) return null;
        return new ClaimReview(op, c.Session.Requester, amount, account.Currency, account.Version,
            Digest(c.Session, amount, account.Currency, account.Version), c.Desk.Status(DurableClaims.Tenant, op).State.ToString());
    }

    // The order: role, separation of duties, then a stored decision for exactly what was shown
    // (a retry is answered from it), then the claim's state, then version and digest.
    private static (int, string) Approve(ClaimsApi.Context c, ApprovalRequest request)
    {
        if (request.Decision is not ("approve" or "reject")) return (400, "decision_is_approve_or_reject");
        if (!c.Caller.Permissions.Contains(Roles.FinanceReviewer)) return (403, "reviewer_not_authorized");
        if (c.Caller.SubjectId == c.Session.Requester) return (403, "separation_of_duties");
        Guid op = c.Session.OperationId;
        string currency = c.Desk.Store.LoadAccount(DurableClaims.Tenant, c.Session.Requester)!.Currency;
        if (c.Durable.LatestDecision(op) is { } stored && Digest(c.Session, stored.Amount, currency, stored.AccountVersion) == request.Digest)
            return Replay(stored, c.Caller.SubjectId, request);
        if (c.Desk.Status(DurableClaims.Tenant, op).State != ClaimState.AwaitingReview) return (409, "not_awaiting_review");
        if (Review(c) is not { } now) return (404, "not_found");
        if (request.AccountVersion != now.AccountVersion) return (409, "stale_version");
        if (request.Digest != now.Digest) return (409, "digest_mismatch");
        if (!c.Durable.RecordDecision(op, c.Caller.SubjectId, request.Decision == "approve", now.Amount, now.AccountVersion, DateTimeOffset.UtcNow))
            return c.Durable.LatestDecision(op) is { } winner ? Replay(winner, c.Caller.SubjectId, request) : (409, "already_decided");
        return (200, request.Decision == "approve" ? "approved" : "rejected");
    }

    private static (int, string) Replay(DecisionRow stored, string subject, ApprovalRequest request) =>
        stored.ReviewerId == subject && stored.Approve == (request.Decision == "approve")
            ? (200, (stored.Approve ? "approved" : "rejected") + " (replayed)")
            : (409, "already_decided");

    private static string Digest(SessionRow s, decimal amount, string currency, long accountVersion) =>
        ReimbursementPolicy.Digest(DurableClaims.Tenant, s.Requester,
            new ReimbursementProposal(s.OperationId, s.Requester, amount, currency, accountVersion));

    /// <summary>v5's claim state as the interface's operation state. Only Delivered and Reconciled are Completed.</summary>
    public static (OperationState State, string Reason) ToOperation(ClaimStatus status, DecisionRow? decision, DateTimeOffset now) =>
        status.State switch
        {
            ClaimState.AwaitingReview when decision is { } d && d.ExpiresAt <= now => (OperationState.Expired, "approval_expired"),
            ClaimState.AwaitingReview when decision is { } d =>
                (OperationState.Running, d.Approve ? "approved_awaiting_payment" : "rejected_awaiting_worker"),
            ClaimState.AwaitingReview => (OperationState.AwaitingApproval, status.Reason),
            ClaimState.Submitted => (OperationState.Running, status.Reason),
            ClaimState.Committed => (OperationState.Running, "committed_awaiting_delivery"),
            ClaimState.Delivered => (OperationState.Completed, "paid"),
            ClaimState.Reconciled => (OperationState.Completed, "reconciled"),
            ClaimState.OutcomeUnknown => (OperationState.OutcomeUnknown, status.Reason),
            _ => (OperationState.Failed, status.Reason),   // Rejected
        };
}

public sealed record ClaimReview(Guid OperationId, string Requester, decimal Amount, string Currency,
    long AccountVersion, string Digest, string State);

/// <summary>Only what the reviewer saw (its version and digest) and the decision; the reviewer is the caller.</summary>
public sealed record ApprovalRequest(long AccountVersion, string Digest, string Decision);

/// <summary>
/// Turns v5's claim states into status events: one run per claim, a state
/// event each time the mapped state changes. v5's worker is another process,
/// so a background loop polls the claims a client has opened. The log is in
/// memory: a restarted API starts new runs (see the README).
/// </summary>
public sealed class ClaimFeed : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, (string Run, OperationState State, string Reason)> _claims = new();

    public OperationLog Log { get; } = new();
    public (string AppDb, string Journal) Store { get; set; }
    public TimeSpan Every { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Called after an authorized load: start following the claim, record its state now.</summary>
    public bool Track(ClaimsApi.Context c)
    {
        Observe(c.Session.OperationId, c.Desk.Status(DurableClaims.Tenant, c.Session.OperationId), c.Durable.LatestDecision(c.Session.OperationId));
        return true;
    }

    private void Observe(Guid op, ClaimStatus status, DecisionRow? decision)
    {
        (OperationState state, string reason) = InterfaceApi.ToOperation(status, decision, DateTimeOffset.UtcNow);
        string id = op.ToString("D");
        lock (_claims)
        {
            if (!_claims.TryGetValue(op, out var last)) last = (Log.StartRun(id), OperationState.Running, "run_started");
            if (last.State != state || last.Reason != reason) Log.SetState(id, last.Run, state, reason);
            _claims[op] = (last.Run, state, reason);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Every);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (Store.AppDb is null) continue;
            foreach ((Guid op, var last) in _claims)
            {
                if (OperationStates.IsTerminal(last.State)) continue;
                DurableClaims durable = new(Store.AppDb);
                ReimbursementDesk desk = new(new VerifiedProposals(), Store.AppDb, new FakePaymentProvider(Store.Journal),
                    durable.LoadDirectory(), () => DateTimeOffset.UtcNow);
                Observe(op, desk.Status(DurableClaims.Tenant, op), durable.LatestDecision(op));
            }
        }
    }
}
