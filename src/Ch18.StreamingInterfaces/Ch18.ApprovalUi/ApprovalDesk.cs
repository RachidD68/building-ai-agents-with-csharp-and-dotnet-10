using System.Globalization;
using Ch08.ActionPolicy;
using Ch08.IdempotentCommit;
using Microsoft.Data.Sqlite;

namespace Ch18.ApprovalUi;

public enum ProposalStatus { Pending, Approved, Rejected, RevisionRequested, Superseded }

/// <summary>What the reviewer sees: the exact arguments, the version and the digest that bind them.</summary>
public sealed record ReviewView(Guid ProposalId, int Version, string Digest, string Requester,
    decimal Amount, string Currency, long AccountVersion, DateTimeOffset ExpiresAt, string Status);

/// <summary>
/// The reviewer's POST: which version they saw (and its digest) and the
/// decision. No amount, no requester, no reviewer: the server loads those.
/// </summary>
public sealed record DecisionRequest(int Version, string Digest, string Decision, string? Note = null);

/// <summary>HTTP status, the decision's result or the refusal reason, and whether it was a replay.</summary>
public sealed record DecisionResult(int Status, string Result, bool Replayed = false)
{
    public override string ToString() => $"{Status} {Result}{(Replayed ? " (replayed)" : "")}";
}

/// <summary>
/// Proposals and reviewers' decisions in one SQLite file (each call opens its
/// own connection, so concurrent POSTs are separate connections). One
/// decision per (proposal, version): the primary key decides a double click.
/// </summary>
public sealed class ApprovalDesk
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS proposals (
            proposal_id     TEXT    NOT NULL,
            version         INTEGER NOT NULL,
            tenant          TEXT    NOT NULL,
            requester       TEXT    NOT NULL,
            operation_id    TEXT    NOT NULL,
            amount          TEXT    NOT NULL,
            currency        TEXT    NOT NULL,
            account_version INTEGER NOT NULL,
            expires_at      TEXT    NOT NULL,
            status          TEXT    NOT NULL,
            PRIMARY KEY (proposal_id, version));
        CREATE TABLE IF NOT EXISTS decisions (
            proposal_id TEXT    NOT NULL,
            version     INTEGER NOT NULL,
            reviewer    TEXT    NOT NULL,
            decision    TEXT    NOT NULL,
            digest      TEXT    NOT NULL,
            note        TEXT,
            decided_at  TEXT    NOT NULL,
            PRIMARY KEY (proposal_id, version));
        """;

    private readonly string _path;
    private readonly Func<string, string, IReadOnlySet<string>> _rightsOf;
    private readonly Func<DateTimeOffset> _clock;

    /// <param name="rightsOf">The identity system's answer for (tenant, subject), asked on every call.</param>
    public ApprovalDesk(string databasePath, Func<string, string, IReadOnlySet<string>> rightsOf, Func<DateTimeOffset> clock)
    {
        (_path, _rightsOf, _clock) = (databasePath, rightsOf, clock);
        Execute(Schema);
        Commits = new OperationStore(databasePath);
        Commits.Initialize();
    }

    /// <summary>The authorized-actions chapter's idempotent commit, in the same file.</summary>
    public OperationStore Commits { get; }

    /// <summary>A new proposal at version 1, built by the application from the model's arguments.</summary>
    public Guid Propose(string tenant, string requester, ReimbursementProposal args)
    {
        Guid id = Guid.NewGuid();
        Insert(id, 1, tenant, requester, args);
        return id;
    }

    /// <summary>
    /// Changed arguments: a new version, and the old one can no longer be
    /// decided or committed. A rejected proposal stays rejected (false).
    /// </summary>
    public bool Revise(Guid proposalId, ReimbursementProposal args, out int version)
    {
        Stored current = Load(proposalId) ?? throw new InvalidOperationException("no such proposal");
        version = current.Version;
        if (current.Status == ProposalStatus.Rejected || Committed(current.Tenant, current.Args.OperationId)) return false;
        Execute("UPDATE proposals SET status = $s WHERE proposal_id = $id AND version = $v",
            ("$s", nameof(ProposalStatus.Superseded)), ("$id", proposalId.ToString("D")), ("$v", current.Version));
        version = current.Version + 1;
        Insert(proposalId, version, current.Tenant, current.Requester, args);
        return true;
    }

    /// <summary>The latest version's arguments, as the application stored them.</summary>
    public ReimbursementProposal? Arguments(Guid proposalId) => Load(proposalId)?.Args;

    /// <summary>The review screen, for a finance reviewer of the proposal's tenant; anyone else gets null (404).</summary>
    public ReviewView? View(string tenant, string subject, Guid proposalId)
    {
        if (Load(proposalId) is not { } p || p.Tenant != tenant || !IsReviewer(tenant, subject)) return null;
        return new ReviewView(proposalId, p.Version, Digest(p), p.Requester, p.Args.Amount, p.Args.Currency,
            p.Args.ExpectedVersion, p.ExpiresAt, p.Status.ToString());
    }

    /// <summary>
    /// The reviewer is (tenant, subject) from the validated token. The checks,
    /// in order: the proposal exists in the caller's tenant (else 404); the
    /// caller is a finance reviewer now (403); not the requester (403); the
    /// version is the latest (409 stale_version) and the digest matches it
    /// (409 digest_mismatch); a decision already stored for that version is
    /// returned as it is when it is the same one (200, replayed) and refused
    /// otherwise (409 already_decided); then expiry (410).
    /// </summary>
    public DecisionResult Decide(string tenant, string subject, Guid proposalId, DecisionRequest request)
    {
        if (Load(proposalId) is not { } p || p.Tenant != tenant) return new(404, "not_found");
        if (request.Decision is not ("approve" or "reject" or "revise")) return new(400, "decision_is_approve_reject_or_revise");
        if (!IsReviewer(tenant, subject)) return new(403, "reviewer_not_authorized");
        if (subject == p.Requester) return new(403, "separation_of_duties");
        if (request.Version != p.Version) return new(409, "stale_version");
        if (!string.Equals(request.Digest, Digest(p), StringComparison.Ordinal)) return new(409, "digest_mismatch");
        if (StoredDecision(proposalId, p.Version) is { } existing) return Replay(existing, subject, request);
        if (_clock() >= p.ExpiresAt) return new(410, "expired");

        int inserted = Execute("""
            INSERT INTO decisions (proposal_id, version, reviewer, decision, digest, note, decided_at)
            VALUES ($id, $v, $reviewer, $decision, $digest, $note, $at)
            ON CONFLICT (proposal_id, version) DO NOTHING
            """, ("$id", proposalId.ToString("D")), ("$v", p.Version), ("$reviewer", subject), ("$decision", request.Decision),
            ("$digest", request.Digest), ("$note", (object?)request.Note ?? DBNull.Value), ("$at", _clock().ToString("O")));
        if (inserted == 0) return Replay(StoredDecision(proposalId, p.Version)!, subject, request);   // lost the race
        ProposalStatus status = request.Decision switch
        {
            "approve" => ProposalStatus.Approved,
            "reject" => ProposalStatus.Rejected,
            _ => ProposalStatus.RevisionRequested,
        };
        Execute("UPDATE proposals SET status = $s WHERE proposal_id = $id AND version = $v",
            ("$s", status.ToString()), ("$id", proposalId.ToString("D")), ("$v", p.Version));
        return new(200, status.ToString());
    }

    /// <summary>
    /// The commit: the ch. 8 policy with the approval loaded from this store
    /// (never from the client), then the ch. 8 idempotent commit. A second
    /// call returns the stored outcome.
    /// </summary>
    public (PolicyResult Policy, CommitResult? Commit) Commit(Guid proposalId, Caller requester)
    {
        Stored p = Load(proposalId) ?? throw new InvalidOperationException("no such proposal");
        if (Committed(p.Tenant, p.Args.OperationId))   // a repeat: the stored outcome, not a second policy run
            return (new PolicyResult(Decision.Allow, "already_committed"), Commits.Submit(p.Tenant, p.Requester, p.Args, _clock()));
        EmployeeAccount account = Commits.LoadAccount(p.Tenant, p.Args.EmployeeId)
            ?? throw new InvalidOperationException("no account");
        ApprovalRecord? approval = StoredDecision(proposalId, p.Version) is { } d && d.Decision != "revise"
            ? new ApprovalRecord(d.Digest, p.Tenant, p.Requester, d.Reviewer,
                IsReviewer(p.Tenant, d.Reviewer) ? Roles.FinanceReviewer : "none",
                ReimbursementPolicy.Version, p.ExpiresAt, d.Decision == "approve")
            : null;
        PolicyResult policy = ReimbursementPolicy.Evaluate(requester, account, p.Args, approval, _clock());
        return policy.Decision == Decision.Allow ? (policy, Commits.Submit(p.Tenant, p.Requester, p.Args, _clock())) : (policy, null);
    }

    private bool Committed(string tenant, Guid operationId)
    {
        using SqliteConnection db = Open();
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM operations WHERE tenant = $t AND operation_id = $op";
        cmd.Parameters.AddWithValue("$t", tenant);
        cmd.Parameters.AddWithValue("$op", operationId.ToString("D"));
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    public int DecisionCount(Guid proposalId)
    {
        using SqliteConnection db = Open();
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM decisions WHERE proposal_id = $id";
        cmd.Parameters.AddWithValue("$id", proposalId.ToString("D"));
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static DecisionResult Replay(Decided existing, string subject, DecisionRequest request) =>
        existing.Reviewer == subject && existing.Decision == request.Decision
            ? new(200, Result(existing.Decision), Replayed: true)
            : new(409, "already_decided");

    private static string Result(string decision) => decision switch
    {
        "approve" => nameof(ProposalStatus.Approved),
        "reject" => nameof(ProposalStatus.Rejected),
        _ => nameof(ProposalStatus.RevisionRequested),
    };

    private bool IsReviewer(string tenant, string subject) => _rightsOf(tenant, subject).Contains(Roles.FinanceReviewer);

    private static string Digest(Stored p) => ReimbursementPolicy.Digest(p.Tenant, p.Requester, p.Args);

    private sealed record Stored(int Version, string Tenant, string Requester, ReimbursementProposal Args,
        DateTimeOffset ExpiresAt, ProposalStatus Status);

    private sealed record Decided(string Reviewer, string Decision, string Digest);

    private void Insert(Guid id, int version, string tenant, string requester, ReimbursementProposal a)
    {
        if (a.EmployeeId != requester) throw new ArgumentException("a reimbursement is proposed for its requester", nameof(a));
        Execute("""
        INSERT INTO proposals (proposal_id, version, tenant, requester, operation_id, amount, currency, account_version, expires_at, status)
        VALUES ($id, $v, $tenant, $requester, $op, $amount, $currency, $account, $expires, $status)
        """, ("$id", id.ToString("D")), ("$v", version), ("$tenant", tenant), ("$requester", requester),
        ("$op", a.OperationId.ToString("D")), ("$amount", ReimbursementPolicy.CanonicalAmount(a.Amount)), ("$currency", a.Currency),
        ("$account", a.ExpectedVersion), ("$expires", (_clock() + Lifetime).ToString("O")), ("$status", nameof(ProposalStatus.Pending)));
    }

    // The latest version of a proposal, loaded server-side.
    private Stored? Load(Guid id)
    {
        using SqliteConnection db = Open();
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT version, tenant, requester, operation_id, amount, currency, account_version, expires_at, status
            FROM proposals WHERE proposal_id = $id ORDER BY version DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        using SqliteDataReader r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        ReimbursementProposal args = new(Guid.Parse(r.GetString(3)), r.GetString(2),
            decimal.Parse(r.GetString(4), CultureInfo.InvariantCulture), r.GetString(5), r.GetInt64(6));
        return new Stored(r.GetInt32(0), r.GetString(1), r.GetString(2), args,
            DateTimeOffset.Parse(r.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            Enum.Parse<ProposalStatus>(r.GetString(8)));
    }

    private Decided? StoredDecision(Guid id, int version)
    {
        using SqliteConnection db = Open();
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = "SELECT reviewer, decision, digest FROM decisions WHERE proposal_id = $id AND version = $v";
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        cmd.Parameters.AddWithValue("$v", version);
        using SqliteDataReader r = cmd.ExecuteReader();
        return r.Read() ? new Decided(r.GetString(0), r.GetString(1), r.GetString(2)) : null;
    }

    private SqliteConnection Open()
    {
        SqliteConnection db = new($"Data Source={_path};Default Timeout=30");
        db.Open();
        return db;
    }

    private int Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using SqliteConnection db = Open();
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return cmd.ExecuteNonQuery();
    }
}
