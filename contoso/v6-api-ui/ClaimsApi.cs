using Ch08.ActionPolicy;
using Ch08.UncertainOutcome;
using Ch17.AspNetCoreHost;
using Contoso.AuthorizedReimbursement;
using Contoso.DurableResume;
using Contoso.Receipts;

namespace Contoso.ApiUi;

/// <summary>
/// v5's durable claims over HTTP. The caller is the token's (tenant, subject),
/// mapped to the identity system's rights at the moment of the request: the
/// v5 store's identity_rights, loaded fresh, so a revoked role is gone at once.
/// The reviewer of a decision is the caller. No request body can name one.
/// </summary>
public static class ClaimsApi
{
    public static string DefaultStore => Path.Combine(Path.GetTempPath(), "AgenticAI", "Contoso.DurableResume");

    public static WebApplication Map(WebApplication app, string store)
    {
        Directory.CreateDirectory(store);
        string appDb = Path.Combine(store, "app.db");
        string journal = Path.Combine(store, "provider.db");

        // Status: the requester, or a finance reviewer of the claim's tenant. Anyone else gets 404.
        app.MapGet("/claims/{id}", (string id, HttpContext http) =>
        {
            if (Open(http, appDb, journal, id) is not { } c) return Results.NotFound();
            ClaimStatus s = c.Desk.Status(DurableClaims.Tenant, c.Session.OperationId);
            return Results.Ok(new ClaimView(c.Session.OperationId, c.Session.Requester, s.State.ToString(), s.Reason, s.Detail));
        }).RequireAuthorization().RequireRateLimiting("per-caller");

        // A reviewer's decision, bound to the amount of record and the account version (v5's rules).
        app.MapPost("/claims/{id}/decision", (string id, DecisionRequest request, HttpContext http) =>
        {
            if (Open(http, appDb, journal, id) is not { } c) return Results.NotFound();
            if (request.Decision is not ("approve" or "reject")) return Results.BadRequest(new { error = "decision is approve or reject" });
            Guid op = c.Session.OperationId;
            string? refused = c.Desk.Status(DurableClaims.Tenant, op).State != ClaimState.AwaitingReview ? "not_awaiting_review"
                : !c.Caller.Permissions.Contains(Roles.FinanceReviewer) ? "reviewer_not_authorized"
                : c.Caller.SubjectId == c.Session.Requester ? "separation_of_duties"
                : null;
            if (refused is not null)
                return Results.Json(new { refused }, statusCode: refused == "not_awaiting_review" ? 409 : 403);
            decimal amount = c.Desk.AmountOfRecord(DurableClaims.Tenant, op)!.Value;
            long version = c.Desk.Store.LoadAccount(DurableClaims.Tenant, c.Session.Requester)!.Version;
            if (!c.Durable.RecordDecision(op, c.Caller.SubjectId, request.Decision == "approve", amount, version, DateTimeOffset.UtcNow))
                return Results.Json(new { refused = "already_decided" }, statusCode: 409);
            return Results.Ok(new DecisionView(op, request.Decision, c.Caller.SubjectId, amount, version));
        }).RequireAuthorization().RequireRateLimiting("per-caller");
        return app;
    }

    public sealed record Context(DurableClaims Durable, ReimbursementDesk Desk, Caller Caller, SessionRow Session);

    // Authorized load: the claim is found only for a caller who may see it.
    internal static Context? Open(HttpContext http, string appDb, string journal, string id)
    {
        if (CallerIdentity.From(http.User) is not { } who || !Guid.TryParse(id, out Guid op)) return null;
        DurableClaims durable = new(appDb);
        if (durable.LoadSession(op.ToString("D")) is not { } session) return null;
        PermissionDirectory directory = durable.LoadDirectory();
        Caller caller = directory.Authenticate(who.TenantId, who.SubjectId);
        bool visible = who.TenantId == DurableClaims.Tenant
            && (who.SubjectId == session.Requester || caller.Permissions.Contains(Roles.FinanceReviewer));
        if (!visible) return null;
        ReimbursementDesk desk = new(new VerifiedProposals(), appDb, new FakePaymentProvider(journal), directory, () => DateTimeOffset.UtcNow);
        return new Context(durable, desk, caller, session);
    }
}

/// <summary>Only the decision. The reviewer is the caller; a "reviewer" field in the JSON is ignored.</summary>
public sealed record DecisionRequest(string Decision);

public sealed record ClaimView(Guid OperationId, string Requester, string State, string Reason, string? Detail);

public sealed record DecisionView(Guid OperationId, string Decision, string Reviewer, decimal Amount, long AccountVersion);
