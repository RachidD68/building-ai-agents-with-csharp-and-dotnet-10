// ============================================================================
// Contoso v5: the durable part.
//
//   present --> [contoso-review port] --> pay --> settle
//   (--graph next adds an audit step before the port)
//
// The claim itself lives in the v2 desk (app.db). v5 adds, in the same file:
//   roles      the identity system's answer, so every process loads it fresh
//   decisions  a reviewer's decision, bound to the amount of record and the
//              account version the reviewer saw
//   sessions   where each paused review is: session, checkpoint, graph version
//   leases     who may finish a claim now (Ch15 LeaseStore, fencing token)
// The checkpoints are in checkpoints/ (FileSystemJsonCheckpointStore) and the
// payment provider's journal in provider.db (v2's FakePaymentProvider).
// ============================================================================

using System.Globalization;
using Ch08.ActionPolicy;
using Ch15.ExternalEffects;
using Contoso.AuthorizedReimbursement;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Data.Sqlite;

namespace Contoso.DurableResume;

public sealed record ClaimTicket(Guid OperationId, string Requester, decimal Amount, string Currency, long AccountVersion);
public sealed record ReviewRequest(Guid OperationId, string Requester, decimal Amount, string Currency, long AccountVersion);

/// <summary>A decision as the paused workflow receives it: who, what, and the claim version it covers.</summary>
public sealed record ReviewVerdict(Guid OperationId, string ReviewerId, bool Approve, decimal ShownAmount, long AccountVersion);

public sealed record ClaimResult(Guid OperationId, string State, string Reason, string? PaymentId, string Detail);

public sealed record DecisionRow(Guid OperationId, string ReviewerId, bool Approve, decimal Amount, long AccountVersion,
    DateTimeOffset ExpiresAt);

public sealed record SessionRow(Guid OperationId, string Requester, string SessionId, string CheckpointId, string GraphVersion);

/// <summary>This process's handles on the durable state, passed to the pay step.</summary>
public sealed record PayContext(ReimbursementDesk Desk, PermissionDirectory Directory, LeaseStore Leases, long Token,
    IStepHook Hook, Func<DateTimeOffset> Clock);

public static class ContosoReviewGraph
{
    public const string RulesVersion = "contoso-v5-rules-1";

    public static Workflow Build(PayContext? pay, bool next = false)
    {
        ExecutorBinding present = new PresentExecutor().BindExecutor();
        ExecutorBinding port = RequestPort.Create<ReviewRequest, ReviewVerdict>("contoso-review").BindAsExecutor();
        ExecutorBinding payStep = new PayExecutor(pay).BindExecutor();
        ExecutorBinding settle = new SettleExecutor().BindExecutor();
        WorkflowBuilder builder = new(present);
        if (next)
        {
            ExecutorBinding audit = new AuditExecutor().BindExecutor();
            builder.AddEdge(present, audit).AddEdge(audit, port);
        }
        else
        {
            builder.AddEdge(present, port);
        }
        return builder.AddEdge(port, payStep).AddEdge(payStep, settle)
            .WithOutputFrom(settle).WithName("ContosoDurableReview").Build();
    }
}

internal sealed class PresentExecutor() : Executor<ClaimTicket, ReviewRequest>("present")
{
    public override ValueTask<ReviewRequest> HandleAsync(ClaimTicket t, IWorkflowContext context, CancellationToken ct)
        => ValueTask.FromResult(new ReviewRequest(t.OperationId, t.Requester, t.Amount, t.Currency, t.AccountVersion));
}

internal sealed class AuditExecutor() : Executor<ReviewRequest, ReviewRequest>("audit")
{
    public override ValueTask<ReviewRequest> HandleAsync(ReviewRequest r, IWorkflowContext context, CancellationToken ct)
        => ValueTask.FromResult(r);
}

/// <summary>
/// Hands the decision to the v2 desk, which commits once and pays once. Runs
/// again after any crash, so it reads the claim first: a claim that already
/// left AwaitingReview is reported as it stands, never reviewed twice.
/// </summary>
internal sealed class PayExecutor(PayContext? ctx) : Executor<ReviewVerdict, ClaimResult>("pay")
{
    public override async ValueTask<ClaimResult> HandleAsync(ReviewVerdict v, IWorkflowContext context, CancellationToken ct)
    {
        PayContext c = ctx ?? throw new InvalidOperationException("the pay step runs only in resume");
        ClaimStatus before = c.Desk.Status(DurableClaims.Tenant, v.OperationId);
        if (before.State != ClaimState.AwaitingReview)
            return Result(before, $"re-read the claim: already {before.State}; no second review, no second payment");

        await c.Hook.ReachAsync(StepPoints.BeforeCommit);
        if (!c.Leases.IsCurrent(DurableClaims.LeaseKey(v.OperationId), c.Token, c.Clock()))
            return new ClaimResult(v.OperationId, "StaleLease", "stale_lease", null,
                $"lease token {c.Token} is no longer current; nothing committed");

        Caller reviewer = c.Directory.Authenticate(DurableClaims.Tenant, v.ReviewerId);
        ClaimStatus after = await c.Desk.ReviewAsync(reviewer, v.OperationId, v.ShownAmount, v.Approve, editedAmount: null, ct);
        await c.Hook.ReachAsync(StepPoints.AfterRecord);
        return Result(after, $"reviewed by {v.ReviewerId} through the v2 desk");
    }

    private static ClaimResult Result(ClaimStatus s, string detail) =>
        new(s.OperationId, s.State.ToString(), s.Reason, s.Detail, detail);
}

[YieldsOutput(typeof(ClaimResult))]
internal sealed class SettleExecutor() : Executor<ClaimResult>("settle")
{
    public override ValueTask HandleAsync(ClaimResult r, IWorkflowContext context, CancellationToken ct)
        => context.YieldOutputAsync(r, ct);
}

/// <summary>The v5 tables next to the v2 desk's, in app.db. Short transactions only.</summary>
public sealed class DurableClaims
{
    public const string Tenant = "contoso";
    public static readonly TimeSpan ApprovalLifetime = TimeSpan.FromHours(24);

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS identity_rights (
            subject TEXT NOT NULL,
            right   TEXT NOT NULL,
            PRIMARY KEY (subject, right));
        CREATE TABLE IF NOT EXISTS review_decisions (
            seq             INTEGER PRIMARY KEY AUTOINCREMENT,
            operation_id    TEXT NOT NULL,
            reviewer        TEXT NOT NULL,
            approve         INTEGER NOT NULL,
            amount          TEXT NOT NULL,
            account_version INTEGER NOT NULL,
            expires_at      TEXT NOT NULL,
            UNIQUE (operation_id, amount, account_version));
        CREATE TABLE IF NOT EXISTS review_sessions (
            seq           INTEGER PRIMARY KEY AUTOINCREMENT,
            operation_id  TEXT NOT NULL UNIQUE,
            requester     TEXT NOT NULL,
            session_id    TEXT NOT NULL,
            checkpoint_id TEXT NOT NULL,
            graph_version TEXT NOT NULL);
        """;

    private readonly string _path;

    public DurableClaims(string databasePath)
    {
        _path = databasePath;
        Execute(Schema);
        Leases = new LeaseStore(databasePath);
    }

    public LeaseStore Leases { get; }

    public static string LeaseKey(Guid operationId) => $"claim:{operationId:D}";

    /// <summary>The scenario's people, as v4: alice submits and also reviews, bob submits, rita reviews.</summary>
    public void SeedIdentities() => Execute($"""
        INSERT OR IGNORE INTO identity_rights (subject, right) VALUES
            ('E-1001', '{Permissions.SubmitReimbursement}'), ('E-1001', '{Roles.FinanceReviewer}'),
            ('E-2002', '{Permissions.SubmitReimbursement}'), ('rita', '{Roles.FinanceReviewer}')
        """);

    public void Revoke(string subject, string right) =>
        Execute("DELETE FROM identity_rights WHERE subject = $s AND right = $r", ("$s", subject), ("$r", right));

    /// <summary>The directory as the identity system answers it now: loaded fresh by every process.</summary>
    public PermissionDirectory LoadDirectory()
    {
        PermissionDirectory directory = new();
        using SqliteConnection db = Open();
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = "SELECT subject, right FROM identity_rights";
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read()) directory.Grant(Tenant, r.GetString(0), r.GetString(1));
        return directory;
    }

    /// <summary>One decision per claim version (amount and account version); the first one wins.</summary>
    public bool RecordDecision(Guid op, string reviewer, bool approve, decimal amount, long accountVersion, DateTimeOffset now) => Execute("""
        INSERT INTO review_decisions (operation_id, reviewer, approve, amount, account_version, expires_at)
        VALUES ($op, $reviewer, $approve, $amount, $version, $expires)
        ON CONFLICT (operation_id, amount, account_version) DO NOTHING
        """, ("$op", op.ToString("D")), ("$reviewer", reviewer), ("$approve", approve ? 1 : 0),
        ("$amount", ReimbursementPolicy.CanonicalAmount(amount)), ("$version", accountVersion),
        ("$expires", LeaseStore.Iso(now + ApprovalLifetime))) == 1;

    public DecisionRow? LatestDecision(Guid op)
    {
        using SqliteConnection db = Open();
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT reviewer, approve, amount, account_version, expires_at FROM review_decisions
            WHERE operation_id = $op ORDER BY seq DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$op", op.ToString("D"));
        using SqliteDataReader r = cmd.ExecuteReader();
        return r.Read()
            ? new DecisionRow(op, r.GetString(0), r.GetInt64(1) == 1, decimal.Parse(r.GetString(2), CultureInfo.InvariantCulture),
                r.GetInt64(3), DateTimeOffset.Parse(r.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))
            : null;
    }

    public void SaveSession(SessionRow s) => Execute("""
        INSERT INTO review_sessions (operation_id, requester, session_id, checkpoint_id, graph_version)
        VALUES ($op, $requester, $session, $checkpoint, $graph)
        ON CONFLICT (operation_id) DO UPDATE SET session_id = excluded.session_id,
            checkpoint_id = excluded.checkpoint_id, graph_version = excluded.graph_version
        """, ("$op", s.OperationId.ToString("D")), ("$requester", s.Requester), ("$session", s.SessionId),
        ("$checkpoint", s.CheckpointId), ("$graph", s.GraphVersion));

    /// <summary>A session by operation id, or the newest one for "latest".</summary>
    public SessionRow? LoadSession(string idOrLatest)
    {
        using SqliteConnection db = Open();
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = "SELECT operation_id, requester, session_id, checkpoint_id, graph_version FROM review_sessions " +
                          (idOrLatest == "latest" ? "ORDER BY seq DESC LIMIT 1" : "WHERE operation_id = $op");
        cmd.Parameters.AddWithValue("$op", idOrLatest);
        using SqliteDataReader r = cmd.ExecuteReader();
        return r.Read()
            ? new SessionRow(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4))
            : null;
    }

    /// <summary>The provider's journal (provider.db): one row per simulated payment.</summary>
    public static IReadOnlyList<string> JournalReferences(string providerPath)
    {
        if (!File.Exists(providerPath)) return [];
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = providerPath, Pooling = false, Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        db.Open();
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'payments'";
        if (cmd.ExecuteScalar() is null) return [];
        cmd.CommandText = "SELECT reference FROM payments ORDER BY accepted_at";
        using SqliteDataReader r = cmd.ExecuteReader();
        List<string> refs = [];
        while (r.Read()) refs.Add(r.GetString(0));
        return refs;
    }

    private int Execute(string sql, params (string, object)[] parameters)
    {
        using SqliteConnection db = Open();
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path, Pooling = false, DefaultTimeout = 30,
        }.ToString());
        db.Open();
        return db;
    }
}
