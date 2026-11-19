using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Ch08.ActionPolicy;

/// <summary>
/// The authenticated caller, rebuilt by the server from the auth context for
/// every evaluation. Never take it from the model's arguments: a tenant id the
/// model wrote is not evidence of anything. Because permissions are loaded
/// fresh each time, a right revoked after the proposal is refused at commit.
/// </summary>
public sealed record Caller(string TenantId, string SubjectId, IReadOnlySet<string> Permissions);

/// <summary>The employee's account as the application stores it (scoped load, current version).</summary>
public sealed record EmployeeAccount(
    string TenantId, string EmployeeId, string Currency,
    decimal ReimbursableBalance, long Version);

/// <summary>
/// What the model proposed, plus two values the application adds: the
/// operation id (issued once, when the claim is submitted) and the account
/// version the proposal was built against. A proposal is data, not a command.
/// </summary>
public sealed record ReimbursementProposal(
    Guid OperationId, string EmployeeId, decimal Amount,
    string Currency, long ExpectedVersion);

/// <summary>
/// A reviewer's decision, written by the server after it authenticated the
/// reviewer, and loaded back from that trusted store. Never accept one that
/// was deserialized from the model or the client: the digest binds values
/// together; it does not prove who approved them.
/// </summary>
public sealed record ApprovalRecord(
    string Digest, string TenantId, string RequesterId,
    string ReviewerId, string ReviewerRole,
    string PolicyVersion, DateTimeOffset ExpiresAtUtc, bool Approved);

public enum Decision { Deny, RequireApproval, Allow }

public sealed record PolicyResult(Decision Decision, string Reason);

public static class Permissions
{
    public const string SubmitReimbursement = "reimbursement.submit";
}

public static class Roles
{
    public const string FinanceReviewer = "finance-reviewer";
}

public static class ReimbursementPolicy
{
    public const string Version = "reimbursement-v1";
    public const decimal LowValueLimit = 50m;

    // A deterministic pre-execution check. It does not move money, reserve the
    // balance, consume the approval or make the operation idempotent: run it
    // again inside the transaction that commits (see Ch08.IdempotentCommit).
    public static PolicyResult Evaluate(
        Caller caller, EmployeeAccount account, ReimbursementProposal proposal,
        ApprovalRecord? approval, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(caller.SubjectId) ||
            !caller.Permissions.Contains(Permissions.SubmitReimbursement))
            return Deny("missing_permission");
        if (string.IsNullOrWhiteSpace(caller.TenantId) || caller.TenantId != account.TenantId ||
            proposal.EmployeeId != account.EmployeeId)
            return Deny("resource_scope");
        if (proposal.OperationId == Guid.Empty)
            return Deny("missing_operation_id");
        if (proposal.Amount <= 0m || proposal.Amount > account.ReimbursableBalance)
            return Deny("invalid_amount");
        if (CurrencyCode.Normalize(proposal.Currency) is not { } currency ||
            currency != CurrencyCode.Normalize(account.Currency))
            return Deny("currency_mismatch");
        if (proposal.ExpectedVersion != account.Version)
            return Deny("stale_version");
        if (proposal.Amount <= LowValueLimit)
            return new(Decision.Allow, "low_value_policy");
        if (approval is null)
            return new(Decision.RequireApproval, "approval_missing");
        if (!approval.Approved)
            return Deny("approval_rejected");
        if (string.IsNullOrWhiteSpace(approval.ReviewerId) || approval.ReviewerRole != Roles.FinanceReviewer)
            return Deny("reviewer_not_authorized");
        if (approval.ReviewerId == approval.RequesterId || approval.ReviewerId == caller.SubjectId)
            return Deny("separation_of_duties");
        if (approval.TenantId != caller.TenantId || approval.RequesterId != caller.SubjectId ||
            approval.PolicyVersion != Version || approval.ExpiresAtUtc <= now ||
            approval.Digest != Digest(caller.TenantId, caller.SubjectId, proposal))
            return Deny("approval_invalid");
        return new(Decision.Allow, "exact_proposal_approved");
    }

    /// <summary>
    /// SHA-256 over a canonical form of everything the approval covers. The
    /// amount is written without trailing zeros and in the invariant culture,
    /// so 100m and 100.00m give the same digest on every machine; the currency
    /// is normalized first. It binds values; it is not a signature.
    /// </summary>
    public static string Digest(string tenantId, string requesterId, ReimbursementProposal proposal)
    {
        string[] canonical =
        [
            Version, tenantId, requesterId,
            proposal.OperationId.ToString("D"),
            proposal.EmployeeId,
            CanonicalAmount(proposal.Amount),
            CurrencyCode.Normalize(proposal.Currency) ?? proposal.Currency,
            proposal.ExpectedVersion.ToString(CultureInfo.InvariantCulture),
        ];
        // A JSON array of strings is unambiguous: no separator can be forged.
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical)));
    }

    public static string CanonicalAmount(decimal amount) =>
        amount.ToString("0.############################", CultureInfo.InvariantCulture);

    private static PolicyResult Deny(string reason) => new(Decision.Deny, reason);
}

/// <summary>
/// Currency codes compared in one form: trimmed, upper case (invariant), and
/// shaped like an ISO 4217 alphabetic code (three ASCII letters). Anything
/// else is not a currency. The account's currency stays the authority.
/// </summary>
public static class CurrencyCode
{
    public static string? Normalize(string? code)
    {
        string value = (code ?? "").Trim().ToUpperInvariant();
        return value.Length == 3 && value.All(char.IsAsciiLetterUpper) ? value : null;
    }
}
