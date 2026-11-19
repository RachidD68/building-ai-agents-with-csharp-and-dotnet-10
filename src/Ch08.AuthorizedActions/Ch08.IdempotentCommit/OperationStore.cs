using System.Globalization;
using System.Text.Json;
using Ch08.ActionPolicy;
using Microsoft.Data.Sqlite;

namespace Ch08.IdempotentCommit;

public enum CommitStatus { Committed, Replayed, Conflict, StaleVersion, InsufficientBalance, UnknownAccount }

/// <summary>What one submit did. Only <see cref="CommitStatus.Committed"/> changed state.</summary>
public sealed record CommitResult(CommitStatus Status, Guid OperationId, string Outcome);

/// <summary>
/// The application's side of the boundary: one SQLite file holding the
/// accounts, every committed operation under its business key, and an outbox
/// of effects still to deliver. Each submit opens its own connection, so
/// concurrent callers really are separate connections to the same file.
/// </summary>
public sealed class OperationStore(string databasePath)
{
    public const string Schema = """
        CREATE TABLE IF NOT EXISTS accounts (
            tenant        TEXT    NOT NULL,
            employee_id   TEXT    NOT NULL,
            currency      TEXT    NOT NULL,
            balance       TEXT    NOT NULL,          -- decimal, invariant culture
            version       INTEGER NOT NULL,
            PRIMARY KEY (tenant, employee_id));
        CREATE TABLE IF NOT EXISTS operations (
            tenant        TEXT NOT NULL,
            operation_id  TEXT NOT NULL,
            args_digest   TEXT NOT NULL,
            outcome       TEXT NOT NULL,
            committed_at  TEXT NOT NULL,
            UNIQUE (tenant, operation_id));
        CREATE TABLE IF NOT EXISTS outbox (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            tenant        TEXT NOT NULL,
            operation_id  TEXT NOT NULL,
            payload       TEXT NOT NULL,
            created_at    TEXT NOT NULL,
            dispatched_at TEXT);
        """;

    public string DatabasePath { get; } = databasePath;

    public void Initialize()
    {
        using SqliteConnection db = Open();
        Execute(db, null, Schema);
    }

    /// <summary>
    /// Commits the proposal at most once per (tenant, operation id). The same
    /// key with the same arguments returns the stored outcome; the same key
    /// with other arguments is a conflict. Nothing changes unless the account
    /// is still at the version the proposal was built and approved against.
    /// </summary>
    public CommitResult Submit(string tenant, string requesterId, ReimbursementProposal p, DateTimeOffset now)
    {
        string digest = ReimbursementPolicy.Digest(tenant, requesterId, p);
        string op = p.OperationId.ToString("D");
        using SqliteConnection db = Open();
        using SqliteTransaction tx = db.BeginTransaction(deferred: false);   // BEGIN IMMEDIATE: one writer

        // 1. Claim the business key. The UNIQUE constraint decides the race.
        int claimed = Execute(db, tx, """
            INSERT INTO operations (tenant, operation_id, args_digest, outcome, committed_at)
            VALUES ($tenant, $op, $digest, '', $now)
            ON CONFLICT (tenant, operation_id) DO NOTHING
            """, ("$tenant", tenant), ("$op", op), ("$digest", digest), ("$now", now.ToString("O")));
        if (claimed == 0)
        {
            tx.Rollback();
            return ReadWinner(db, tenant, op, digest);          // the losing racer reads the winner's row
        }

        // 2. Recheck the account inside the same transaction.
        (decimal balance, long version)? account = ReadAccount(db, tx, tenant, p.EmployeeId);
        CommitStatus? refused = account switch
        {
            null => CommitStatus.UnknownAccount,
            { version: var v } when v != p.ExpectedVersion => CommitStatus.StaleVersion,
            { balance: var b } when p.Amount > b => CommitStatus.InsufficientBalance,
            _ => null,
        };
        if (refused is { } status)
        {
            tx.Rollback();                                       // releases the key: no effect at all
            return new(status, p.OperationId, $"refused: {status}");
        }

        // 3. State change, stored outcome and outbox row: one commit, or none.
        decimal newBalance = account!.Value.balance - p.Amount;
        long newVersion = p.ExpectedVersion + 1;
        int updated = Execute(db, tx, """
            UPDATE accounts SET balance = $balance, version = $newVersion
            WHERE tenant = $tenant AND employee_id = $employee AND version = $expected
            """, ("$balance", Amount(newBalance)), ("$newVersion", newVersion), ("$tenant", tenant),
            ("$employee", p.EmployeeId), ("$expected", p.ExpectedVersion));
        if (updated != 1) throw new InvalidOperationException("account changed inside the transaction");
        string outcome = $"reimbursed {Amount(p.Amount)} {CurrencyCode.Normalize(p.Currency)} to {p.EmployeeId}; " +
                         $"balance {Amount(newBalance)}, version {newVersion}";
        Execute(db, tx, "UPDATE operations SET outcome = $outcome WHERE tenant = $tenant AND operation_id = $op",
            ("$outcome", outcome), ("$tenant", tenant), ("$op", op));
        Execute(db, tx, """
            INSERT INTO outbox (tenant, operation_id, payload, created_at)
            VALUES ($tenant, $op, $payload, $now)
            """, ("$tenant", tenant), ("$op", op), ("$payload", OutboxPayload(p)), ("$now", now.ToString("O")));
        tx.Commit();
        return new(CommitStatus.Committed, p.OperationId, outcome);
    }

    public void SeedAccount(EmployeeAccount account)
    {
        using SqliteConnection db = Open();
        Execute(db, null, """
            INSERT OR REPLACE INTO accounts (tenant, employee_id, currency, balance, version)
            VALUES ($tenant, $employee, $currency, $balance, $version)
            """, ("$tenant", account.TenantId), ("$employee", account.EmployeeId),
            ("$currency", CurrencyCode.Normalize(account.Currency) ?? account.Currency),
            ("$balance", Amount(account.ReimbursableBalance)), ("$version", account.Version));
    }

    /// <summary>The account as stored now (a scoped load: tenant and employee).</summary>
    public EmployeeAccount? LoadAccount(string tenant, string employeeId)
    {
        using SqliteConnection db = Open();
        using SqliteCommand cmd = Command(db, null,
            "SELECT currency, balance, version FROM accounts WHERE tenant = $tenant AND employee_id = $employee",
            ("$tenant", tenant), ("$employee", employeeId));
        using SqliteDataReader r = cmd.ExecuteReader();
        return r.Read()
            ? new EmployeeAccount(tenant, employeeId, r.GetString(0),
                decimal.Parse(r.GetString(1), CultureInfo.InvariantCulture), r.GetInt64(2))
            : null;
    }

    public long Count(string table)
    {
        if (table is not ("accounts" or "operations" or "outbox")) throw new ArgumentException(table);
        using SqliteConnection db = Open();
        using SqliteCommand cmd = Command(db, null, $"SELECT COUNT(*) FROM {table}");
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Outbox rows not yet handed to the external provider, oldest first.</summary>
    public IReadOnlyList<OutboxRow> PendingOutbox()
    {
        using SqliteConnection db = Open();
        using SqliteCommand cmd = Command(db, null,
            "SELECT id, tenant, operation_id, payload FROM outbox WHERE dispatched_at IS NULL ORDER BY id");
        using SqliteDataReader r = cmd.ExecuteReader();
        List<OutboxRow> rows = [];
        while (r.Read())
            rows.Add(new OutboxRow(r.GetInt64(0), r.GetString(1), Guid.Parse(r.GetString(2)),
                JsonSerializer.Deserialize<OutboxPayloadRecord>(r.GetString(3))!));
        return rows;
    }

    public void MarkDispatched(long outboxId, DateTimeOffset now)
    {
        using SqliteConnection db = Open();
        Execute(db, null, "UPDATE outbox SET dispatched_at = $now WHERE id = $id",
            ("$now", now.ToString("O")), ("$id", outboxId));
    }

    private CommitResult ReadWinner(SqliteConnection db, string tenant, string op, string digest)
    {
        using SqliteCommand cmd = Command(db, null,
            "SELECT args_digest, outcome FROM operations WHERE tenant = $tenant AND operation_id = $op",
            ("$tenant", tenant), ("$op", op));
        using SqliteDataReader r = cmd.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException($"operation {op} vanished after a key conflict");
        Guid id = Guid.Parse(op);
        return r.GetString(0) == digest
            ? new(CommitStatus.Replayed, id, r.GetString(1))
            : new(CommitStatus.Conflict, id, "refused: the operation id was already used with other arguments");
    }

    private static (decimal balance, long version)? ReadAccount(
        SqliteConnection db, SqliteTransaction tx, string tenant, string employeeId)
    {
        using SqliteCommand cmd = Command(db, tx,
            "SELECT balance, version FROM accounts WHERE tenant = $tenant AND employee_id = $employee",
            ("$tenant", tenant), ("$employee", employeeId));
        using SqliteDataReader r = cmd.ExecuteReader();
        return r.Read() ? (decimal.Parse(r.GetString(0), CultureInfo.InvariantCulture), r.GetInt64(1)) : null;
    }

    private static string OutboxPayload(ReimbursementProposal p) => JsonSerializer.Serialize(
        new OutboxPayloadRecord(p.EmployeeId, Amount(p.Amount), CurrencyCode.Normalize(p.Currency) ?? p.Currency));

    private static string Amount(decimal value) => ReimbursementPolicy.CanonicalAmount(value);

    private SqliteConnection Open()
    {
        // No pooling: every Open() is a new physical connection, which is what
        // the concurrency test needs to be honest about "two connections".
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Pooling = false,
            DefaultTimeout = 30,            // seconds a writer waits for the lock
        };
        var db = new SqliteConnection(cs.ToString());
        db.Open();
        return db;
    }

    private static int Execute(SqliteConnection db, SqliteTransaction? tx, string sql,
        params (string Name, object Value)[] parameters)
    {
        using SqliteCommand cmd = Command(db, tx, sql, parameters);
        return cmd.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql,
        params (string Name, object Value)[] parameters)
    {
        SqliteCommand cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach ((string name, object value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }
}

/// <summary>The effect still to deliver, as written in the same transaction as the state change.</summary>
public sealed record OutboxPayloadRecord(string EmployeeId, string Amount, string Currency);

public sealed record OutboxRow(long Id, string Tenant, Guid OperationId, OutboxPayloadRecord Payload);
