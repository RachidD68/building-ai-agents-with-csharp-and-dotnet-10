using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Ch15.ExternalEffects;

/// <summary>
/// The business record of one payout. The operation id is issued once, when
/// the claim is approved, and never again: every attempt, replay and
/// reconciliation uses it. State: Approved, Paying, Paid, Unknown, Cancelled, Conflict.
/// </summary>
public sealed record PayoutRow(string ClaimId, string Tenant, Guid OperationId, string EmployeeId, string Amount,
    string Currency, string State, long Fence, string? PaymentId, string? SessionId, string? CheckpointId,
    bool CancelRequested);

/// <summary>
/// The business record (payouts) and the leases on claims (<see cref="LeaseStore"/>). The
/// delivery worker of Ch08.UncertainOutcome keeps its deliveries table in the
/// same file. Every write that commits work on a claim checks, in the same
/// statement, that the caller's fencing token is the current, unexpired lease.
/// Transactions are short; no lock is held across a provider call.
/// </summary>
public sealed class PayoutStore
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS payouts (
            claim_id         TEXT PRIMARY KEY,
            tenant           TEXT NOT NULL,
            operation_id     TEXT NOT NULL,
            employee_id      TEXT NOT NULL,
            amount           TEXT NOT NULL,
            currency         TEXT NOT NULL,
            state            TEXT NOT NULL,
            fence            INTEGER NOT NULL DEFAULT 0,
            payment_id       TEXT,
            session_id       TEXT,
            checkpoint_id    TEXT,
            cancel_requested INTEGER NOT NULL DEFAULT 0);
        """;

    /// <summary>The lease condition every fenced write repeats, keyed by the claim.</summary>
    private const string Fenced = LeaseStore.FencedSql;

    public PayoutStore(string databasePath)
    {
        DatabasePath = databasePath;
        Execute(Schema);
        Leases = new LeaseStore(databasePath);
    }

    public string DatabasePath { get; }

    /// <summary>Leases on claims, in the same file, so a fenced write checks its token in its own statement.</summary>
    public LeaseStore Leases { get; }

    /// <summary>Approves a claim for payout: the business record, with its operation id. Idempotent per claim.</summary>
    public PayoutRow Approve(string claimId, string tenant, string employeeId, decimal amount, string currency)
    {
        Execute("""
            INSERT INTO payouts (claim_id, tenant, operation_id, employee_id, amount, currency, state)
            VALUES ($claim, $tenant, $op, $employee, $amount, $currency, 'Approved')
            ON CONFLICT (claim_id) DO NOTHING
            """, ("$claim", claimId), ("$tenant", tenant), ("$op", Guid.NewGuid().ToString("D")), ("$employee", employeeId),
            ("$amount", amount.ToString("0.00", CultureInfo.InvariantCulture)), ("$currency", currency));
        return Load(claimId)!;
    }

    /// <summary>
    /// Takes the lease if nobody holds an unexpired one. The token goes up by
    /// one on every takeover, so a worker that lost the lease holds an old token.
    /// Returns null while another owner's lease is still valid.
    /// </summary>
    public Lease? TryAcquireLease(string claimId, string owner, DateTimeOffset now, TimeSpan duration) =>
        Leases.TryAcquire(claimId, owner, now, duration);

    public Lease? LoadLease(string claimId) => Leases.Load(claimId);

    /// <summary>Gives the lease back after a run. A dead worker never calls this: its lease expires.</summary>
    public void ReleaseLease(string claimId, long token, DateTimeOffset now) => Leases.Release(claimId, token, now);

    /// <summary>Records where the workflow can restart this payout: the checkpoint taken before the payout step.</summary>
    public void SaveRestartPoint(string claimId, string sessionId, string checkpointId) => Execute("""
        UPDATE payouts SET session_id = $session, checkpoint_id = $checkpoint
        WHERE claim_id = $claim AND checkpoint_id IS NULL
        """, ("$session", sessionId), ("$checkpoint", checkpointId), ("$claim", claimId));

    /// <summary>
    /// Fenced: Approved, Paying or Unknown -> Paying, stamped with the caller's
    /// token. False when the token is stale or the payout is already final.
    /// </summary>
    public bool BeginPayout(string claimId, long token, DateTimeOffset now) => Execute($"""
        UPDATE payouts SET state = 'Paying', fence = $token
        WHERE claim_id = $claim AND state IN ('Approved', 'Paying', 'Unknown') AND {Fenced}
        """, ("$claim", claimId), ("$key", claimId), ("$token", token), ("$now", Iso(now))) == 1;

    /// <summary>Fenced: writes the payout's outcome, only for the token that began it and still holds the lease.</summary>
    public bool RecordPayout(string claimId, long token, string state, string? paymentId, DateTimeOffset now) => Execute($"""
        UPDATE payouts SET state = $state, payment_id = $payment
        WHERE claim_id = $claim AND fence = $token AND {Fenced}
        """, ("$state", state), ("$payment", (object?)paymentId ?? DBNull.Value), ("$claim", claimId),
        ("$key", claimId), ("$token", token), ("$now", Iso(now))) == 1;

    /// <summary>Fenced: a payout not yet begun is cancelled. One that began is not: its outcome must be known first.</summary>
    public bool Cancel(string claimId, long token, DateTimeOffset now) => Execute($"""
        UPDATE payouts SET state = 'Cancelled', fence = $token
        WHERE claim_id = $claim AND state = 'Approved' AND {Fenced}
        """, ("$claim", claimId), ("$key", claimId), ("$token", token), ("$now", Iso(now))) == 1;

    /// <summary>The requester asks to cancel. Only a flag: the worker decides what a cancel can still do.</summary>
    public void RequestCancel(string claimId) =>
        Execute("UPDATE payouts SET cancel_requested = 1 WHERE claim_id = $claim", ("$claim", claimId));

    /// <summary>The naive step's write, unfenced, with whatever operation id it minted (the defect).</summary>
    public void RecordNaive(string claimId, Guid operationId, string state, string? paymentId) => Execute("""
        UPDATE payouts SET state = $state, payment_id = $payment, operation_id = $op WHERE claim_id = $claim
        """, ("$state", state), ("$payment", (object?)paymentId ?? DBNull.Value), ("$op", operationId.ToString("D")),
        ("$claim", claimId));

    public PayoutRow? Load(string claimId)
    {
        using SqliteConnection db = Open();
        using SqliteCommand cmd = Command(db, """
            SELECT tenant, operation_id, employee_id, amount, currency, state, fence, payment_id, session_id,
                   checkpoint_id, cancel_requested
            FROM payouts WHERE claim_id = $claim
            """, ("$claim", claimId));
        using SqliteDataReader r = cmd.ExecuteReader();
        return r.Read()
            ? new PayoutRow(claimId, r.GetString(0), Guid.Parse(r.GetString(1)), r.GetString(2), r.GetString(3),
                r.GetString(4), r.GetString(5), r.GetInt64(6), Nullable(r, 7), Nullable(r, 8), Nullable(r, 9),
                r.GetInt64(10) == 1)
            : null;
    }

    /// <summary>The simulated provider's journal (its own SQLite file): every row is one effect.</summary>
    public static IReadOnlyList<(string Reference, string PaymentId)> ReadJournal(string providerPath)
    {
        if (!File.Exists(providerPath)) return [];
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = providerPath,
            Pooling = false,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        db.Open();
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'payments'";
        if (cmd.ExecuteScalar() is null) return [];
        cmd.CommandText = "SELECT reference, payment_id FROM payments ORDER BY accepted_at";
        using SqliteDataReader r = cmd.ExecuteReader();
        List<(string, string)> rows = [];
        while (r.Read()) rows.Add((r.GetString(0), r.GetString(1)));
        return rows;
    }

    private static string? Nullable(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static string Iso(DateTimeOffset t) => LeaseStore.Iso(t);

    private int Execute(string sql, params (string, object)[] parameters)
    {
        using SqliteConnection db = Open();
        using SqliteCommand cmd = Command(db, sql, parameters);
        return cmd.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection db, string sql, params (string, object)[] parameters)
    {
        SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString());
        db.Open();
        return db;
    }
}
