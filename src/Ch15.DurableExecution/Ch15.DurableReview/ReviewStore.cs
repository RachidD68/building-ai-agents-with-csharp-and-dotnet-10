using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Ch15.DurableReview;

/// <summary>A claim as the business record holds it now.</summary>
public sealed record ClaimRow(string ClaimId, string Requester, decimal Amount, string Currency, int Version, string State);

/// <summary>A recorded decision, bound to the claim version the reviewer saw.</summary>
public sealed record DecisionRow(string ClaimId, int ClaimVersion, string ReviewerId, ReviewDecision Decision,
    DateTimeOffset DecidedAt, DateTimeOffset ExpiresAt);

/// <summary>Where a paused review lives: the checkpoint to resume and the graph version that wrote it.</summary>
public sealed record SessionRow(string ClaimId, int ClaimVersion, string SessionId, string CheckpointId, string GraphVersion);

/// <summary>
/// The application's durable state, in one SQLite file next to the checkpoint
/// folder: claims, reviewer roles (a stand-in for the identity system),
/// decisions and the pointer to each paused review. Every method opens its
/// own connection and runs one short transaction; nothing is held while a
/// reviewer thinks.
/// </summary>
public sealed class ReviewStore
{
    public const string ReviewerRole = "finance-reviewer";
    public static readonly TimeSpan ApprovalLifetime = TimeSpan.FromHours(24);

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS claims (
            claim_id  TEXT PRIMARY KEY,
            requester TEXT NOT NULL,
            amount    TEXT NOT NULL,
            currency  TEXT NOT NULL,
            version   INTEGER NOT NULL,
            state     TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS roles (
            subject TEXT NOT NULL,
            role    TEXT NOT NULL,
            PRIMARY KEY (subject, role));
        CREATE TABLE IF NOT EXISTS decisions (
            claim_id      TEXT NOT NULL,
            claim_version INTEGER NOT NULL,
            reviewer      TEXT NOT NULL,
            decision      TEXT NOT NULL,
            decided_at    TEXT NOT NULL,
            expires_at    TEXT NOT NULL,
            PRIMARY KEY (claim_id, claim_version));
        CREATE TABLE IF NOT EXISTS sessions (
            claim_id      TEXT PRIMARY KEY,
            claim_version INTEGER NOT NULL,
            session_id    TEXT NOT NULL,
            checkpoint_id TEXT NOT NULL,
            graph_version TEXT NOT NULL);
        """;

    private readonly string _path;

    public ReviewStore(string databasePath)
    {
        _path = databasePath;
        using SqliteConnection db = Open();
        Execute(db, null, Schema);
    }

    /// <summary>The demo's people: rita and sam review; alice and bob only submit. Idempotent.</summary>
    public void SeedRoles() => Execute(
        "INSERT OR IGNORE INTO roles (subject, role) VALUES ('rita', $role), ('sam', $role)", ("$role", ReviewerRole));

    public void Revoke(string subject, string role) =>
        Execute("DELETE FROM roles WHERE subject = $s AND role = $r", ("$s", subject), ("$r", role));

    public bool HasRole(string subject, string role) =>
        Scalar("SELECT COUNT(*) FROM roles WHERE subject = $s AND role = $r", ("$s", subject), ("$r", role)) == 1;

    /// <summary>Creates the claim at version 1, or returns the existing one unchanged.</summary>
    public ClaimRow CreateOrLoadClaim(string claimId, string requester, decimal amount, string currency)
    {
        Execute("""
            INSERT INTO claims (claim_id, requester, amount, currency, version, state)
            VALUES ($id, $requester, $amount, $currency, 1, 'AwaitingReview')
            ON CONFLICT (claim_id) DO NOTHING
            """, ("$id", claimId), ("$requester", requester), ("$amount", Money(amount)), ("$currency", currency));
        return LoadClaim(claimId)!;
    }

    /// <summary>The requester changes the amount while the review waits: a new version of the claim.</summary>
    public bool Amend(string claimId, decimal amount) => Execute("""
        UPDATE claims SET amount = $amount, version = version + 1
        WHERE claim_id = $id AND state = 'AwaitingReview'
        """, ("$amount", Money(amount)), ("$id", claimId)) == 1;

    public ClaimRow? LoadClaim(string claimId)
    {
        using SqliteConnection db = Open();
        using SqliteCommand cmd = Command(db, null,
            "SELECT requester, amount, currency, version, state FROM claims WHERE claim_id = $id", ("$id", claimId));
        using SqliteDataReader r = cmd.ExecuteReader();
        return r.Read()
            ? new ClaimRow(claimId, r.GetString(0), decimal.Parse(r.GetString(1), CultureInfo.InvariantCulture),
                r.GetString(2), r.GetInt32(3), r.GetString(4))
            : null;
    }

    /// <summary>
    /// A reviewer decides on the claim as it is now. Checked in the same
    /// transaction as the write: the claim waits for review, the reviewer holds
    /// the role, the reviewer is not the requester, and nobody decided this
    /// version yet (first writer wins). Returns null when stored, else the reason.
    /// </summary>
    public string? RecordDecision(string claimId, string reviewerId, ReviewDecision decision, DateTimeOffset now)
    {
        using SqliteConnection db = Open();
        using SqliteTransaction tx = db.BeginTransaction(deferred: false);
        ClaimRow? claim = null;
        using (SqliteCommand load = Command(db, tx,
                   "SELECT requester, amount, currency, version, state FROM claims WHERE claim_id = $id", ("$id", claimId)))
        using (SqliteDataReader r = load.ExecuteReader())
        {
            if (r.Read())
                claim = new ClaimRow(claimId, r.GetString(0), decimal.Parse(r.GetString(1), CultureInfo.InvariantCulture),
                    r.GetString(2), r.GetInt32(3), r.GetString(4));
        }
        string? refused = claim is null ? "unknown_claim"
            : claim.State != "AwaitingReview" ? "not_awaiting_review"
            : Scalar(db, tx, "SELECT COUNT(*) FROM roles WHERE subject = $s AND role = $r",
                ("$s", reviewerId), ("$r", ReviewerRole)) != 1 ? "reviewer_not_authorized"
            : reviewerId == claim.Requester ? "separation_of_duties"
            : null;
        if (refused is not null) return refused;

        int stored = Execute(db, tx, """
            INSERT INTO decisions (claim_id, claim_version, reviewer, decision, decided_at, expires_at)
            VALUES ($id, $version, $reviewer, $decision, $now, $expires)
            ON CONFLICT (claim_id, claim_version) DO NOTHING
            """, ("$id", claimId), ("$version", claim!.Version), ("$reviewer", reviewerId),
            ("$decision", decision.ToString()), ("$now", now.ToString("O")), ("$expires", (now + ApprovalLifetime).ToString("O")));
        tx.Commit();
        return stored == 1 ? null : "already_decided";
    }

    /// <summary>The newest decision for the claim, whatever version it covers.</summary>
    public DecisionRow? LatestDecision(string claimId)
    {
        using SqliteConnection db = Open();
        using SqliteCommand cmd = Command(db, null, """
            SELECT claim_version, reviewer, decision, decided_at, expires_at FROM decisions
            WHERE claim_id = $id ORDER BY claim_version DESC LIMIT 1
            """, ("$id", claimId));
        using SqliteDataReader r = cmd.ExecuteReader();
        return r.Read()
            ? new DecisionRow(claimId, r.GetInt32(0), r.GetString(1), Enum.Parse<ReviewDecision>(r.GetString(2)),
                Time(r.GetString(3)), Time(r.GetString(4)))
            : null;
    }

    /// <summary>
    /// Everything a stored decision must still satisfy when the review resumes,
    /// read from the records as they are now, never from the checkpoint:
    /// it exists, it covers the claim's current version, it has not expired,
    /// and the reviewer still holds the role. Returns the verdict to send, or
    /// the reason the resume is blocked ("no_decision" means: keep waiting).
    /// </summary>
    public (ReviewVerdict? Verdict, string? Blocked) Revalidate(ClaimRow claim, DateTimeOffset now)
    {
        DecisionRow? d = LatestDecision(claim.ClaimId);
        string? blocked = d is null ? "no_decision"
            : d.ClaimVersion != claim.Version ? "stale_approval"
            : now > d.ExpiresAt ? "approval_expired"
            : !HasRole(d.ReviewerId, ReviewerRole) ? "reviewer_revoked"
            : null;
        return blocked is null ? (new ReviewVerdict(claim.ClaimId, d!.ClaimVersion, d.ReviewerId, d.Decision), null) : (null, blocked);
    }

    public void SaveSession(SessionRow s) => Execute("""
        INSERT INTO sessions (claim_id, claim_version, session_id, checkpoint_id, graph_version)
        VALUES ($id, $version, $session, $checkpoint, $graph)
        ON CONFLICT (claim_id) DO UPDATE SET claim_version = excluded.claim_version, session_id = excluded.session_id,
            checkpoint_id = excluded.checkpoint_id, graph_version = excluded.graph_version
        """, ("$id", s.ClaimId), ("$version", s.ClaimVersion), ("$session", s.SessionId),
        ("$checkpoint", s.CheckpointId), ("$graph", s.GraphVersion));

    public SessionRow? LoadSession(string claimId)
    {
        using SqliteConnection db = Open();
        using SqliteCommand cmd = Command(db, null,
            "SELECT claim_version, session_id, checkpoint_id, graph_version FROM sessions WHERE claim_id = $id", ("$id", claimId));
        using SqliteDataReader r = cmd.ExecuteReader();
        return r.Read() ? new SessionRow(claimId, r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3)) : null;
    }

    /// <summary>Records the outcome once: only a claim still waiting, at the version decided, moves.</summary>
    public bool Finish(string claimId, int version, string state) => Execute("""
        UPDATE claims SET state = $state WHERE claim_id = $id AND version = $version AND state = 'AwaitingReview'
        """, ("$state", state), ("$id", claimId), ("$version", version)) == 1;

    public long Count(string table) => Scalar($"SELECT COUNT(*) FROM {table}");

    private static string Money(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    private static DateTimeOffset Time(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private int Execute(string sql, params (string, object)[] parameters)
    {
        using SqliteConnection db = Open();
        return Execute(db, null, sql, parameters);
    }

    private long Scalar(string sql, params (string, object)[] parameters)
    {
        using SqliteConnection db = Open();
        return Scalar(db, null, sql, parameters);
    }

    private static int Execute(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object)[] parameters)
    {
        using SqliteCommand cmd = Command(db, tx, sql, parameters);
        return cmd.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object)[] parameters)
    {
        using SqliteCommand cmd = Command(db, tx, sql, parameters);
        return (long)cmd.ExecuteScalar()!;
    }

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object)[] parameters)
    {
        SqliteCommand cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach ((string name, object value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString());
        db.Open();
        return db;
    }
}
