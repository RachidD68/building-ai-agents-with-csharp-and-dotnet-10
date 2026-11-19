using System.ComponentModel;
using System.Globalization;
using Ch08.ActionPolicy;
using Ch08.IdempotentCommit;
using Ch08.UncertainOutcome;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace Contoso.AuthorizedReimbursement;

/// <summary>
/// An employee's expense claim as submitted. The submission id is created once,
/// when the employee opens the form, so a double-clicked Submit carries the
/// same id twice. It becomes the operation id of the reimbursement.
/// </summary>
public sealed record ExpenseClaim(Guid SubmissionId, string EmployeeId, string Text);

/// <summary>What the model is asked to return. Data to check, not a command.</summary>
public sealed record ProposalDraft(
    [property: Description("Employee id written on the claim, e.g. E-1001")] string EmployeeId,
    [property: Description("Amount to reimburse, a plain number such as 64.80")] decimal Amount,
    [property: Description("ISO 4217 currency code, e.g. EUR")] string Currency,
    [property: Description("One sentence explaining the amount")] string Explanation);

/// <summary>The states a claim shows to the employee and to the reviewer.</summary>
public enum ClaimState { Submitted, AwaitingReview, Rejected, Committed, Delivered, OutcomeUnknown, Reconciled }

public sealed record ClaimStatus(Guid OperationId, ClaimState State, string Reason, string? Detail = null);

/// <summary>
/// Who holds which rights, as the host's identity system would answer. A
/// Caller is built from it at the moment it is needed, so a right revoked
/// after a claim was submitted is missing when the claim is committed.
/// </summary>
public sealed class PermissionDirectory
{
    private readonly Dictionary<(string Tenant, string Subject), HashSet<string>> _rights = [];
    private readonly Lock _gate = new();

    public void Grant(string tenant, string subject, params string[] rights)
    {
        lock (_gate)
        {
            if (!_rights.TryGetValue((tenant, subject), out HashSet<string>? set))
                _rights[(tenant, subject)] = set = [];
            set.UnionWith(rights);
        }
    }

    public void Revoke(string tenant, string subject, string right)
    {
        lock (_gate) _rights.GetValueOrDefault((tenant, subject))?.Remove(right);
    }

    /// <summary>The caller as authentication would produce it now (a copy of the current rights).</summary>
    public Caller Authenticate(string tenant, string subject)
    {
        lock (_gate)
            return new Caller(tenant, subject,
                new HashSet<string>(_rights.GetValueOrDefault((tenant, subject)) ?? []));
    }
}

/// <summary>
/// The Contoso reimbursement path, end to end:
///   claim -> model proposal -> ActionPolicy -> (review) -> IdempotentCommit
///   -> outbox -> payment provider -> OutcomeUnknown -> reconciliation.
/// Identity always comes from the caller the host authenticated, never from
/// the model. Every effect is simulated.
/// </summary>
public sealed class ReimbursementDesk
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS claims (
            tenant           TEXT    NOT NULL,
            operation_id     TEXT    NOT NULL,
            requester        TEXT    NOT NULL,
            employee_id      TEXT,
            amount           TEXT,
            currency         TEXT,
            expected_version INTEGER,
            explanation      TEXT,
            state            TEXT    NOT NULL,
            reason           TEXT    NOT NULL,
            PRIMARY KEY (tenant, operation_id));
        CREATE TABLE IF NOT EXISTS approvals (
            tenant         TEXT    NOT NULL,
            operation_id   TEXT    NOT NULL,
            digest         TEXT    NOT NULL,
            requester      TEXT    NOT NULL,
            reviewer       TEXT    NOT NULL,
            reviewer_role  TEXT    NOT NULL,
            policy_version TEXT    NOT NULL,
            expires_at     TEXT    NOT NULL,
            approved       INTEGER NOT NULL,
            PRIMARY KEY (tenant, operation_id));
        """;

    private readonly IChatClient _model;
    private readonly string _databasePath;
    private readonly PermissionDirectory _directory;
    private readonly Func<DateTimeOffset> _clock;

    public ReimbursementDesk(IChatClient model, string databasePath, IPaymentProvider provider,
        PermissionDirectory directory, Func<DateTimeOffset>? clock = null)
    {
        _model = model;
        _databasePath = databasePath;
        _directory = directory;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        Store = new OperationStore(databasePath);
        Store.Initialize();
        Worker = new DeliveryWorker(databasePath, provider);
        Execute(Schema);
    }

    public OperationStore Store { get; }
    public DeliveryWorker Worker { get; }

    /// <summary>The employee submits a claim. A repeated submission returns the claim's current state.</summary>
    public async Task<ClaimStatus> SubmitAsync(Caller employee, ExpenseClaim claim, CancellationToken ct)
    {
        // Intake claims the submission id first: a double click is answered
        // from the stored state, without a second model call.
        if (!ClaimIntake(employee, claim.SubmissionId)) return Status(employee.TenantId, claim.SubmissionId);

        ProposalDraft? draft = await ProposeAsync(claim, ct);
        if (draft is null) return Finish(employee, claim.SubmissionId, ClaimState.Rejected, "unreadable_proposal");

        // Scoped load: the account of the AUTHENTICATED employee, at its current version.
        EmployeeAccount? account = Store.LoadAccount(employee.TenantId, employee.SubjectId);
        if (account is null) return Finish(employee, claim.SubmissionId, ClaimState.Rejected, "resource_scope");
        var proposal = new ReimbursementProposal(claim.SubmissionId, draft.EmployeeId, draft.Amount,
            draft.Currency, account.Version);
        SaveProposal(employee, proposal, draft.Explanation);

        PolicyResult policy = ReimbursementPolicy.Evaluate(employee, account, proposal, approval: null, _clock());
        return policy.Decision switch
        {
            Decision.Deny => Finish(employee, claim.SubmissionId, ClaimState.Rejected, policy.Reason),
            Decision.RequireApproval => Finish(employee, claim.SubmissionId, ClaimState.AwaitingReview, policy.Reason),
            _ => await CommitAsync(employee, proposal, approval: null, ct),
        };
    }

    /// <summary>
    /// A reviewer decides, optionally with an edited amount. The reviewer's role
    /// comes from the authenticated caller, the checks run before anything is
    /// stored, and an edit is stored only together with a valid decision.
    /// </summary>
    public async Task<ClaimStatus> ReviewAsync(Caller reviewer, string tenant, Guid operationId,
        bool approve, decimal? editedAmount, CancellationToken ct)
    {
        (Caller requester, ReimbursementProposal proposal)? pending = LoadProposal(tenant, operationId);
        if (pending is null || Status(tenant, operationId).State != ClaimState.AwaitingReview)
            return Status(tenant, operationId);
        (Caller requester, ReimbursementProposal proposal) = pending.Value;
        if (editedAmount is { } amount) proposal = proposal with { Amount = amount };   // not stored yet

        string role = reviewer.Permissions.Contains(Roles.FinanceReviewer) ? Roles.FinanceReviewer : "";
        var approval = new ApprovalRecord(
            ReimbursementPolicy.Digest(requester.TenantId, requester.SubjectId, proposal),
            requester.TenantId, requester.SubjectId, reviewer.SubjectId, role,
            ReimbursementPolicy.Version, _clock().AddHours(24), approve);
        // Who may decide is checked for a rejection too, so evaluate as if approving.
        PolicyResult check = ReimbursementPolicy.Evaluate(requester,
            Store.LoadAccount(requester.TenantId, requester.SubjectId)!, proposal,
            approval with { Approved = true }, _clock());
        if (check.Reason is "separation_of_duties" or "reviewer_not_authorized")
            return Status(tenant, operationId) with { Reason = check.Reason };   // nothing stored: still waiting

        // One decision per claim: the first valid review claims the approval row
        // and stores the reviewed amount in the same transaction. A second
        // reviewer, or the same click twice, gets the claim's state instead.
        if (!RecordReview(approval, proposal, editedAmount is null ? null : $"amount edited by {reviewer.SubjectId}"))
            return Status(tenant, operationId);
        if (!approve) return Finish(requester, operationId, ClaimState.Rejected, "approval_rejected");
        return await CommitAsync(requester, proposal, approval, ct);
    }

    /// <summary>The amount currently on record for a claim, or null when there is no proposal.</summary>
    public decimal? AmountOfRecord(string tenant, Guid operationId) => LoadProposal(tenant, operationId)?.Item2.Amount;

    /// <summary>After a restart: settle every payment whose outcome is unknown.</summary>
    public async Task<IReadOnlyList<ReconcileResult>> ResumeAsync(CancellationToken ct)
    {
        await DispatchAsync(ct);                      // outbox rows a crash left behind
        return await Worker.ResumeAsync(ct);
    }

    public ClaimStatus Status(string tenant, Guid operationId)
    {
        ClaimState state;
        string reason;
        // Read the claim and close the connection BEFORE asking the delivery
        // worker: an open reader holds a shared lock, and a committing writer on
        // another thread would wait for it while this thread waits for the writer.
        using (SqliteConnection db = Open())
        using (SqliteCommand cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT state, reason FROM claims WHERE tenant = $tenant AND operation_id = $op";
            cmd.Parameters.AddWithValue("$tenant", tenant);
            cmd.Parameters.AddWithValue("$op", operationId.ToString("D"));
            using SqliteDataReader r = cmd.ExecuteReader();
            if (!r.Read()) return new(operationId, ClaimState.Submitted, "unknown_claim");
            state = Enum.Parse<ClaimState>(r.GetString(0));
            reason = r.GetString(1);
        }
        if (state == ClaimState.Committed && Worker.Load(tenant, operationId) is { } delivery)
        {
            state = delivery.Status switch
            {
                DeliveryStatus.Delivered => ClaimState.Delivered,
                DeliveryStatus.OutcomeUnknown or DeliveryStatus.Sending => ClaimState.OutcomeUnknown,
                DeliveryStatus.Reconciled => ClaimState.Reconciled,
                _ => ClaimState.Committed,
            };
            return new(operationId, state, reason, delivery.PaymentId);
        }
        return new(operationId, state, reason);
    }

    private async Task<ProposalDraft?> ProposeAsync(ExpenseClaim claim, CancellationToken ct)
    {
        try
        {
            ChatResponse<ProposalDraft> response = await _model.GetResponseAsync<ProposalDraft>(
                [
                    new ChatMessage(ChatRole.System,
                        "You read Contoso employee expense claims and propose a reimbursement. Return the " +
                        "employee id written on the claim, the amount to reimburse, its ISO currency code and " +
                        "a one-sentence explanation. You only propose; the application decides."),
                    new ChatMessage(ChatRole.User, $"Claim from {claim.EmployeeId}:\n{claim.Text}"),
                ],
                cancellationToken: ct);
            return response.TryGetResult(out ProposalDraft? draft) ? draft : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private async Task<ClaimStatus> CommitAsync(Caller requester, ReimbursementProposal proposal,
        ApprovalRecord? approval, CancellationToken ct)
    {
        // The policy again, against the account as it is now, then one transaction.
        EmployeeAccount account = Store.LoadAccount(requester.TenantId, requester.SubjectId)!;
        PolicyResult policy = ReimbursementPolicy.Evaluate(requester, account, proposal, approval, _clock());
        if (policy.Decision != Decision.Allow)
            return Finish(requester, proposal.OperationId, ClaimState.Rejected, policy.Reason);

        CommitResult commit = Store.Submit(requester.TenantId, requester.SubjectId, proposal, _clock());
        if (commit.Status is CommitStatus.Committed or CommitStatus.Replayed)
            Finish(requester, proposal.OperationId, ClaimState.Committed, policy.Reason);
        else
            return Finish(requester, proposal.OperationId, ClaimState.Rejected, commit.Status.ToString());
        await DispatchAsync(ct);
        return Status(requester.TenantId, proposal.OperationId);
    }

    /// <summary>Hands each outbox row to the delivery worker. A row is marked only after the worker recorded its state.</summary>
    private async Task DispatchAsync(CancellationToken ct)
    {
        foreach (OutboxRow row in Store.PendingOutbox())
        {
            await Worker.DeliverAsync(new Delivery(row.Tenant, row.OperationId, row.Payload.EmployeeId,
                row.Payload.Amount, row.Payload.Currency), ct);
            Store.MarkDispatched(row.Id, _clock());
        }
    }

    private bool ClaimIntake(Caller employee, Guid operationId) => Execute("""
        INSERT INTO claims (tenant, operation_id, requester, state, reason)
        VALUES ($tenant, $op, $requester, 'Submitted', 'received')
        ON CONFLICT (tenant, operation_id) DO NOTHING
        """, ("$tenant", employee.TenantId), ("$op", operationId.ToString("D")), ("$requester", employee.SubjectId)) == 1;

    private void SaveProposal(Caller requester, ReimbursementProposal p, string explanation) => Execute("""
        UPDATE claims SET employee_id = $employee, amount = $amount, currency = $currency,
                          expected_version = $version, explanation = $explanation
        WHERE tenant = $tenant AND operation_id = $op
        """, ("$employee", p.EmployeeId), ("$amount", ReimbursementPolicy.CanonicalAmount(p.Amount)),
        ("$currency", p.Currency), ("$version", p.ExpectedVersion), ("$explanation", explanation),
        ("$tenant", requester.TenantId), ("$op", p.OperationId.ToString("D")));

    private (Caller, ReimbursementProposal)? LoadProposal(string tenant, Guid operationId)
    {
        using SqliteConnection db = Open();
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = "SELECT requester, employee_id, amount, currency, expected_version FROM claims " +
                          "WHERE tenant = $tenant AND operation_id = $op AND amount IS NOT NULL";
        cmd.Parameters.AddWithValue("$tenant", tenant);
        cmd.Parameters.AddWithValue("$op", operationId.ToString("D"));
        using SqliteDataReader r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        // The requester's permissions are reloaded from the directory, so a revoked right is refused at commit.
        Caller requester = _directory.Authenticate(tenant, r.GetString(0));
        return (requester, new ReimbursementProposal(operationId, r.GetString(1),
            decimal.Parse(r.GetString(2), CultureInfo.InvariantCulture), r.GetString(3), r.GetInt64(4)));
    }

    /// <summary>
    /// Stores one review: the approval row (first writer wins) and, in the same
    /// transaction, the reviewed amount. Returns false when a decision already exists.
    /// </summary>
    private bool RecordReview(ApprovalRecord a, ReimbursementProposal p, string? editNote)
    {
        using SqliteConnection db = Open();
        using SqliteTransaction tx = db.BeginTransaction(deferred: false);
        int claimed = Execute(db, tx, """
            INSERT INTO approvals (tenant, operation_id, digest, requester, reviewer, reviewer_role,
                                   policy_version, expires_at, approved)
            VALUES ($tenant, $op, $digest, $requester, $reviewer, $role, $policy, $expires, $approved)
            ON CONFLICT (tenant, operation_id) DO NOTHING
            """, ("$tenant", a.TenantId), ("$op", p.OperationId.ToString("D")), ("$digest", a.Digest),
            ("$requester", a.RequesterId), ("$reviewer", a.ReviewerId), ("$role", a.ReviewerRole),
            ("$policy", a.PolicyVersion), ("$expires", a.ExpiresAtUtc.ToString("O")), ("$approved", a.Approved ? 1 : 0));
        if (claimed == 0)
        {
            tx.Rollback();
            return false;
        }
        Execute(db, tx, """
            UPDATE claims SET amount = $amount, explanation = COALESCE($note, explanation)
            WHERE tenant = $tenant AND operation_id = $op
            """, ("$amount", ReimbursementPolicy.CanonicalAmount(p.Amount)), ("$note", (object?)editNote ?? DBNull.Value),
            ("$tenant", a.TenantId), ("$op", p.OperationId.ToString("D")));
        tx.Commit();
        return true;
    }

    /// <summary>
    /// Moves a claim forward. Only a claim that is still Submitted or
    /// AwaitingReview changes, so a late or duplicate caller can never turn a
    /// Committed claim into Rejected. Returns the state as stored.
    /// </summary>
    private ClaimStatus Finish(Caller requester, Guid operationId, ClaimState state, string reason)
    {
        Execute("""
            UPDATE claims SET state = $state, reason = $reason
            WHERE tenant = $tenant AND operation_id = $op AND state IN ('Submitted', 'AwaitingReview')
            """, ("$state", state.ToString()), ("$reason", reason), ("$tenant", requester.TenantId),
            ("$op", operationId.ToString("D")));
        return Status(requester.TenantId, operationId);
    }

    private int Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using SqliteConnection db = Open();
        return Execute(db, null, sql, parameters);
    }

    private static int Execute(SqliteConnection db, SqliteTransaction? tx, string sql,
        params (string Name, object Value)[] parameters)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach ((string name, object value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString());
        db.Open();
        return db;
    }
}
