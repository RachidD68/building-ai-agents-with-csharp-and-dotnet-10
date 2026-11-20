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
            proposed_amount  TEXT,
            currency         TEXT,
            expected_version INTEGER,
            explanation      TEXT,
            review_note      TEXT,
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
    private readonly SemaphoreSlim _dispatch = new(1, 1);

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
        // from the stored state, without a second model call. Only a claim that
        // never reached the store (proposal_failed, stale_version) is taken again, by one caller.
        if (!ClaimIntake(employee, claim.SubmissionId)) return Status(employee.TenantId, claim.SubmissionId);
        CrashPoint?.Invoke("after_intake");

        ProposalDraft? draft;
        try
        {
            draft = await ProposeAsync(claim, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException ||
                                   (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // The model was unreachable: nothing was proposed. Say so, and let a resubmit try again.
            return Finish(employee, claim.SubmissionId, ClaimState.Rejected, "proposal_failed");
        }
        if (draft is null) return Finish(employee, claim.SubmissionId, ClaimState.Rejected, "unreadable_proposal");

        // Scoped load: the account of the AUTHENTICATED employee, at its current version.
        EmployeeAccount? account = Store.LoadAccount(employee.TenantId, employee.SubjectId);
        if (account is null) return Finish(employee, claim.SubmissionId, ClaimState.Rejected, "resource_scope");
        var proposal = new ReimbursementProposal(claim.SubmissionId, draft.EmployeeId, draft.Amount,
            draft.Currency, account.Version);
        SaveProposal(employee, proposal, draft.Explanation);
        CrashPoint?.Invoke("after_proposal");
        return await DecideAsync(employee, account, proposal, ct);
    }

    /// <summary>The first decision on a stored proposal: rejected, waiting for a reviewer, or committed.</summary>
    private async Task<ClaimStatus> DecideAsync(Caller employee, EmployeeAccount account,
        ReimbursementProposal proposal, CancellationToken ct)
    {
        proposal = Rebase(employee, proposal, account.Version);     // a restart may decide a proposal built earlier
        PolicyResult policy = ReimbursementPolicy.Evaluate(employee, account, proposal, approval: null, _clock());
        return policy.Decision switch
        {
            Decision.Deny => Finish(employee, proposal.OperationId, ClaimState.Rejected, policy.Reason),
            Decision.RequireApproval => Finish(employee, proposal.OperationId, ClaimState.AwaitingReview, policy.Reason),
            _ => await CommitAsync(employee, proposal, approval: null, ct),
        };
    }

    /// <summary>
    /// A reviewer decides on the amount the review screen showed, optionally with
    /// an edited amount. The claim is looked up in the reviewer's own tenant; the
    /// reviewer and the shown amount are checked before anything is stored, and an
    /// edit is stored only together with a valid decision. (The claim's state is
    /// returned to any caller of the tenant who has its id: see the README.)
    /// </summary>
    public async Task<ClaimStatus> ReviewAsync(Caller reviewer, Guid operationId, decimal shownAmount,
        bool approve, decimal? editedAmount, CancellationToken ct)
    {
        string tenant = reviewer.TenantId;                // from authentication, never from the client
        (Caller requester, ReimbursementProposal proposal)? pending = LoadProposal(tenant, operationId);
        if (pending is null || Status(tenant, operationId).State != ClaimState.AwaitingReview)
            return Status(tenant, operationId);
        (Caller requester, ReimbursementProposal proposal) = pending.Value;

        // Who may decide, checked directly: for every amount, and for a rejection too. The decision
        // covers what the reviewer was shown: a proposal that changed since then is shown again.
        string? refused = !reviewer.Permissions.Contains(Roles.FinanceReviewer) ? "reviewer_not_authorized"
            : reviewer.SubjectId == requester.SubjectId ? "separation_of_duties"
            : proposal.Amount != shownAmount ? "proposal_changed" : null;
        if (refused is not null)
            return Status(tenant, operationId) with { Reason = refused };    // nothing stored: still waiting

        // The review covers the account as it is now: another claim may have been paid since the proposal.
        if (Store.LoadAccount(tenant, requester.SubjectId) is not { } account)
            return Finish(requester, operationId, ClaimState.Rejected, "resource_scope");
        proposal = proposal with { Amount = editedAmount ?? proposal.Amount, ExpectedVersion = account.Version };
        var approval = new ApprovalRecord(ReimbursementPolicy.Digest(tenant, requester.SubjectId, proposal),
            tenant, requester.SubjectId, reviewer.SubjectId, Roles.FinanceReviewer,
            ReimbursementPolicy.Version, _clock().AddHours(24), approve);
        // An edited amount must pass the policy before it is stored.
        if (editedAmount is not null && ReimbursementPolicy.Evaluate(requester, account, proposal,
                approval with { Approved = true }, _clock()) is { Decision: Decision.Deny } edit)
            return Status(tenant, operationId) with { Reason = edit.Reason };  // nothing stored: still waiting

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

    /// <summary>After a restart: repair claims a crash left between steps, then settle every unknown payment.</summary>
    public async Task<IReadOnlyList<ReconcileResult>> ResumeAsync(CancellationToken ct)
    {
        await RepairClaimsAsync(ct);                  // claims stranded between review, commit and state
        await DispatchAsync(ct);                      // outbox rows a crash left behind
        return await Worker.ResumeAsync(ct);
    }

    /// <summary>Test hook: called after each step's transaction; the self-test throws from it to simulate a crash.</summary>
    internal Action<string>? CrashPoint { get; set; }

    /// <summary>
    /// Intake, the proposal, the review, the commit and the claim's state are
    /// written in separate transactions, so a crash can leave a claim behind
    /// its own records. Run at startup, before new requests. A claim whose
    /// operation committed is marked Committed; a stored decision with no
    /// operation is finished now (an approval goes through CommitAsync, which
    /// runs the policy again); a Submitted claim with a proposal is decided
    /// now, and one without becomes proposal_failed, which a resubmit retries.
    /// Each claim is repaired on its own: one that fails is logged and left
    /// for the next restart, and the outbox and the unknown payments still run.
    /// </summary>
    private async Task RepairClaimsAsync(CancellationToken ct)
    {
        foreach ((string tenant, Guid op, string requesterId, bool submitted, bool committed, bool? approved)
                 in StrandedClaims())
        {
            try
            {
                await RepairClaimAsync(tenant, op, requesterId, submitted, committed, approved, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Console.Error.WriteLine($"repair of claim {tenant}/{op} failed, left for the next restart: " +
                                        $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private async Task RepairClaimAsync(string tenant, Guid op, string requesterId, bool submitted,
        bool committed, bool? approved, CancellationToken ct)
    {
        if (LoadProposal(tenant, op) is not { } pending)
        {
            if (submitted)
                Finish(_directory.Authenticate(tenant, requesterId), op, ClaimState.Rejected, "proposal_failed");
            return;
        }
        (Caller requester, ReimbursementProposal proposal) = pending;
        if (committed)
            Finish(requester, op, ClaimState.Committed, "committed_before_restart");
        else if (approved == false)
            Finish(requester, op, ClaimState.Rejected, "approval_rejected");
        else if (LoadApproval(tenant, op) is { } approval)
            await CommitAsync(requester, proposal, approval, ct);
        else if (submitted && Store.LoadAccount(tenant, requester.SubjectId) is { } account)
            await DecideAsync(requester, account, proposal, ct);
        else if (submitted)
            Finish(requester, op, ClaimState.Rejected, "resource_scope");
    }

    private List<(string Tenant, Guid OperationId, string Requester, bool Submitted, bool Committed, bool? Approved)>
        StrandedClaims()
    {
        using SqliteConnection db = Open();
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT c.tenant, c.operation_id, c.requester, c.state = 'Submitted',
                   o.operation_id IS NOT NULL, a.approved
            FROM claims c
            LEFT JOIN operations o ON o.tenant = c.tenant AND o.operation_id = c.operation_id
            LEFT JOIN approvals a ON a.tenant = c.tenant AND a.operation_id = c.operation_id
            WHERE c.state = 'Submitted'
               OR (c.state = 'AwaitingReview' AND (o.operation_id IS NOT NULL OR a.approved IS NOT NULL))
            """;
        using SqliteDataReader r = cmd.ExecuteReader();
        List<(string, Guid, string, bool, bool, bool?)> rows = [];
        while (r.Read())
            rows.Add((r.GetString(0), Guid.Parse(r.GetString(1)), r.GetString(2), r.GetInt64(3) == 1,
                r.GetInt64(4) == 1, r.IsDBNull(5) ? null : r.GetInt64(5) == 1));
        return rows;
    }

    private ApprovalRecord? LoadApproval(string tenant, Guid operationId)
    {
        using SqliteConnection db = Open();
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = "SELECT digest, requester, reviewer, reviewer_role, policy_version, expires_at, approved " +
                          "FROM approvals WHERE tenant = $tenant AND operation_id = $op";
        cmd.Parameters.AddWithValue("$tenant", tenant);
        cmd.Parameters.AddWithValue("$op", operationId.ToString("D"));
        using SqliteDataReader r = cmd.ExecuteReader();
        return r.Read()
            ? new ApprovalRecord(r.GetString(0), tenant, r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                DateTimeOffset.Parse(r.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                r.GetInt64(6) == 1)
            : null;
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
            // The provider holds this reference for another payment: committed here, not paid.
            if (delivery.Status == DeliveryStatus.Conflict)
                return new(operationId, ClaimState.Committed, "provider_reference_conflict");
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
        // The reviewer's role is read again from the directory: a role revoked
        // since the review approves nothing. The stored approval only records it.
        if (approval is not null &&
            !_directory.Authenticate(approval.TenantId, approval.ReviewerId).Permissions.Contains(approval.ReviewerRole))
            return Finish(requester, proposal.OperationId, ClaimState.Rejected, "reviewer_not_authorized");

        for (int attempt = 1; ; attempt++)
        {
            // The policy again, against the account as it is now, then one transaction.
            if (Store.LoadAccount(requester.TenantId, requester.SubjectId) is not { } account)
                return Finish(requester, proposal.OperationId, ClaimState.Rejected, "resource_scope");
            if (account.Version != proposal.ExpectedVersion)
            {
                // Another claim of this employee committed first. The approval covered the
                // old version, so a reviewed claim goes back to the reviewer; a low-value
                // claim is rebuilt on the current version and the policy decides again.
                if (approval is not null) return ReviewAgain(requester, proposal.OperationId, account.Version);
                proposal = Rebase(requester, proposal, account.Version);
            }
            PolicyResult policy = ReimbursementPolicy.Evaluate(requester, account, proposal, approval, _clock());
            if (policy.Decision != Decision.Allow)
                return Finish(requester, proposal.OperationId, ClaimState.Rejected, policy.Reason);

            CommitResult commit = Store.Submit(requester.TenantId, requester.SubjectId, proposal, _clock());
            CrashPoint?.Invoke("after_commit");
            if (commit.Status is CommitStatus.Committed or CommitStatus.Replayed)
            {
                Finish(requester, proposal.OperationId, ClaimState.Committed, policy.Reason);
                break;
            }
            // Lost a race to another commit between the check and the transaction: look again.
            if (commit.Status == CommitStatus.StaleVersion && attempt < MaxCommitAttempts) continue;
            return Finish(requester, proposal.OperationId, ClaimState.Rejected, ReasonCode(commit.Status));
        }
        await DispatchAsync(ct);
        return Status(requester.TenantId, proposal.OperationId);
    }

    private const int MaxCommitAttempts = 10;

    /// <summary>The store's refusals as the reason codes the rest of the desk uses.</summary>
    private static string ReasonCode(CommitStatus status) => status switch
    {
        CommitStatus.StaleVersion => "stale_version",
        CommitStatus.InsufficientBalance => "insufficient_balance",
        CommitStatus.Conflict => "operation_conflict",
        CommitStatus.UnknownAccount => "unknown_account",
        _ => "commit_refused",
    };

    /// <summary>Rebuilds a stored proposal on the account's current version (nothing else changes).</summary>
    private ReimbursementProposal Rebase(Caller requester, ReimbursementProposal p, long version)
    {
        if (p.ExpectedVersion == version) return p;
        Execute("""
            UPDATE claims SET expected_version = $version
            WHERE tenant = $tenant AND operation_id = $op AND state IN ('Submitted', 'AwaitingReview')
            """, ("$version", version), ("$tenant", requester.TenantId), ("$op", p.OperationId.ToString("D")));
        return p with { ExpectedVersion = version };
    }

    /// <summary>
    /// The account moved after the review: the claim waits for a reviewer again
    /// (account_changed), on the current version and with the model's proposed
    /// amount, since an earlier reviewer's edit was never approved at this version.
    /// The old decision is removed in the same transaction, so the next valid
    /// review can claim the row.
    /// </summary>
    private ClaimStatus ReviewAgain(Caller requester, Guid operationId, long version)
    {
        using (SqliteConnection db = Open())
        using (SqliteTransaction tx = db.BeginTransaction(deferred: false))
        {
            (string, object)[] key = [("$tenant", requester.TenantId), ("$op", operationId.ToString("D"))];
            int moved = Execute(db, tx, """
                UPDATE claims SET expected_version = $version, reason = 'account_changed',
                                  amount = proposed_amount, review_note = NULL
                WHERE tenant = $tenant AND operation_id = $op AND state = 'AwaitingReview'
                """, [("$version", version), .. key]);
            if (moved == 1)
                Execute(db, tx, "DELETE FROM approvals WHERE tenant = $tenant AND operation_id = $op", key);
            tx.Commit();
        }
        return Status(requester.TenantId, operationId);
    }

    /// <summary>
    /// Hands each outbox row to the delivery worker. A row is marked only after the
    /// worker recorded its state. Each row runs on its own: an error other than the
    /// caller's cancellation is logged, the row is left for the next resume, and the
    /// other rows are still delivered.
    /// </summary>
    private async Task DispatchAsync(CancellationToken ct)
    {
        // One dispatcher at a time in this process: a commit that waits here finds its
        // own row delivered by the dispatcher before it, never half-way through a send.
        await _dispatch.WaitAsync(ct);
        try
        {
            foreach (OutboxRow row in Store.PendingOutbox())
            {
                try
                {
                    await Worker.DeliverAsync(new Delivery(row.Tenant, row.OperationId, row.Payload.EmployeeId,
                        row.Payload.Amount, row.Payload.Currency), ct);
                    Store.MarkDispatched(row.Id, _clock());
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    Console.Error.WriteLine($"delivery of {row.Tenant}/{row.OperationId} failed, left for the next " +
                                            $"resume: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            _dispatch.Release();
        }
    }

    private bool ClaimIntake(Caller employee, Guid operationId) => Execute("""
        INSERT INTO claims (tenant, operation_id, requester, state, reason)
        VALUES ($tenant, $op, $requester, 'Submitted', 'received')
        ON CONFLICT (tenant, operation_id) DO UPDATE SET state = 'Submitted', reason = 'received'
        WHERE claims.state = 'Rejected' AND claims.reason IN ('proposal_failed', 'stale_version')
          AND claims.requester = excluded.requester
        """, ("$tenant", employee.TenantId), ("$op", operationId.ToString("D")), ("$requester", employee.SubjectId)) == 1;

    private void SaveProposal(Caller requester, ReimbursementProposal p, string explanation) => Execute("""
        UPDATE claims SET employee_id = $employee, amount = $amount, proposed_amount = $amount,
                          currency = $currency, expected_version = $version, explanation = $explanation
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
            UPDATE claims SET amount = $amount, expected_version = $version, review_note = $note
            WHERE tenant = $tenant AND operation_id = $op
            """, ("$amount", ReimbursementPolicy.CanonicalAmount(p.Amount)), ("$version", p.ExpectedVersion),
            ("$note", (object?)editNote ?? DBNull.Value), ("$tenant", a.TenantId), ("$op", p.OperationId.ToString("D")));
        tx.Commit();
        CrashPoint?.Invoke("after_review");
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
