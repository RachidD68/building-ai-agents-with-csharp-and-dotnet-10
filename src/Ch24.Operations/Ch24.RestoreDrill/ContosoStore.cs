using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ch08.UncertainOutcome;
using Microsoft.Data.Sqlite;

namespace Ch24.RestoreDrill;

/// <summary>
/// What the store says the service was running with: model, prompt, skills,
/// index and the embedding model the index was built with. Restored with the
/// data, so the binary that reads it can be checked against it.
/// </summary>
public sealed record ConfigurationIdentity(
    string Model, string PromptSha256, string SkillSetSha256, string IndexVersion, string EmbeddingModel)
{
    public IReadOnlyDictionary<string, string> Rows => new SortedDictionary<string, string>(StringComparer.Ordinal)
    {
        ["model"] = Model, ["prompt_sha256"] = PromptSha256, ["skills_sha256"] = SkillSetSha256,
        ["index_version"] = IndexVersion, ["embedding_model"] = EmbeddingModel,
    };

    public static string Digest(IEnumerable<KeyValuePair<string, string>> rows) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("\n", rows.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => $"{r.Key}={r.Value}")))))[..16];
}

/// <summary>
/// Schema history, one step per version, recorded in PRAGMA user_version.
/// v3 changed the MEANING of a column without renaming it: approvals.amount
/// holds minor units (cents) from v3 on. A v2 binary still reads the column,
/// and reads 50.00 EUR as 5000.00 EUR.
/// </summary>
public static class StoreSchema
{
    public const int Current = 3;

    private static readonly string[] Steps =
    [
        // v1: conversation metadata (never transcripts), delivery records, configuration identity
        """
        CREATE TABLE conversations (
            conversation_id TEXT PRIMARY KEY, tenant TEXT NOT NULL, subject TEXT NOT NULL,
            started_at TEXT NOT NULL, last_turn_at TEXT NOT NULL, turns INTEGER NOT NULL);
        CREATE TABLE store_identity (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        """ + DeliveryWorker.Schema + ";",
        // v2: approvals (amount in major units, "50.00") and workflow state
        """
        CREATE TABLE approvals (
            tenant TEXT NOT NULL, operation_id TEXT NOT NULL, approved_by TEXT NOT NULL,
            approved_at TEXT NOT NULL, amount TEXT NOT NULL, PRIMARY KEY (tenant, operation_id));
        CREATE TABLE workflow_state (
            run_id TEXT PRIMARY KEY, step TEXT NOT NULL, checkpoint TEXT NOT NULL, updated_at TEXT NOT NULL);
        """,
        // v3: approvals.amount in minor units ("5000"), same column name
        "UPDATE approvals SET amount = CAST(CAST(ROUND(CAST(amount AS REAL) * 100) AS INTEGER) AS TEXT);",
    ];

    public static void Migrate(SqliteConnection db, int target)
    {
        for (int v = Version(db); v < target; v++)
        {
            using SqliteTransaction tx = db.BeginTransaction();
            Exec(db, tx, Steps[v]);
            Exec(db, tx, $"PRAGMA user_version = {v + 1}");
            tx.Commit();
        }
    }

    public static int Version(SqliteConnection db) => Convert.ToInt32(Scalar(db, "PRAGMA user_version"), CultureInfo.InvariantCulture);

    internal static void Exec(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object)[] p)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach ((string name, object value) in p) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    internal static object? Scalar(SqliteConnection db, string sql, params (string, object)[] p)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object value) in p) cmd.Parameters.AddWithValue(name, value);
        return cmd.ExecuteScalar();
    }
}

/// <summary>The durable stores of the Contoso service, in one SQLite file (WAL mode).</summary>
public sealed class ContosoStore(string path, TimeProvider clock)
{
    public static readonly string[] Tables = ["conversations", "approvals", "deliveries", "workflow_state", "store_identity"];
    public string Path { get; } = path;

    public SqliteConnection Open(bool readOnly = false)
    {
        SqliteConnection db = new(new SqliteConnectionStringBuilder
        {
            DataSource = Path, Pooling = false,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        db.Open();
        return db;
    }

    public void Create(ConfigurationIdentity identity)
    {
        using SqliteConnection db = Open();
        StoreSchema.Scalar(db, "PRAGMA journal_mode = WAL");
        StoreSchema.Migrate(db, StoreSchema.Current);
        foreach ((string k, string v) in identity.Rows)
            StoreSchema.Exec(db, null, "INSERT INTO store_identity (key, value) VALUES ($k, $v)", ("$k", k), ("$v", v));
    }

    public void Conversation(string id, string tenant, string subject)
    {
        string now = Now();
        using SqliteConnection db = Open();
        StoreSchema.Exec(db, null, """
            INSERT INTO conversations VALUES ($id, $tenant, $subject, $now, $now, 1)
            ON CONFLICT (conversation_id) DO UPDATE SET last_turn_at = $now, turns = turns + 1
            """, ("$id", id), ("$tenant", tenant), ("$subject", subject), ("$now", now));
    }

    /// <summary>Records an approval the v3 way: minor units.</summary>
    public void Approve(string tenant, Guid operationId, string approvedBy, decimal amount)
    {
        using SqliteConnection db = Open();
        StoreSchema.Exec(db, null, "INSERT INTO approvals VALUES ($t, $op, $by, $now, $minor)",
            ("$t", tenant), ("$op", operationId.ToString("D")), ("$by", approvedBy), ("$now", Now()),
            ("$minor", decimal.ToInt64(amount * 100m).ToString(CultureInfo.InvariantCulture)));
    }

    public void Checkpoint(string runId, string step, string checkpoint)
    {
        using SqliteConnection db = Open();
        StoreSchema.Exec(db, null, """
            INSERT INTO workflow_state VALUES ($run, $step, $cp, $now)
            ON CONFLICT (run_id) DO UPDATE SET step = $step, checkpoint = $cp, updated_at = $now
            """, ("$run", runId), ("$step", step), ("$cp", checkpoint), ("$now", Now()));
    }

    /// <summary>A delivery that is waiting for something before it may be sent (status Pending).</summary>
    public void HoldDelivery(Delivery d)
    {
        using SqliteConnection db = Open();
        StoreSchema.Exec(db, null, """
            INSERT INTO deliveries (tenant, operation_id, employee_id, amount, currency, status, updated_at)
            VALUES ($t, $op, $e, $a, $c, 'Pending', $now)
            """, ("$t", d.Tenant), ("$op", d.OperationId.ToString("D")), ("$e", d.EmployeeId),
            ("$a", d.Amount), ("$c", d.Currency), ("$now", Now()));
    }

    private string Now() => clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
}

/// <summary>Reads a store without changing it: counts, identity, newest write, approvals.</summary>
public static class StoreFacts
{
    public static SortedDictionary<string, long> Counts(SqliteConnection db)
    {
        SortedDictionary<string, long> counts = new(StringComparer.Ordinal);
        foreach (string t in ContosoStore.Tables)
            counts[t] = TableExists(db, t) ? (long)StoreSchema.Scalar(db, $"SELECT COUNT(*) FROM {t}")! : -1;
        return counts;
    }

    public static bool TableExists(SqliteConnection db, string table) =>
        (long)StoreSchema.Scalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $n", ("$n", table))! == 1;

    public static Dictionary<string, string> Identity(SqliteConnection db)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM store_identity";
        using SqliteDataReader r = cmd.ExecuteReader();
        Dictionary<string, string> rows = new(StringComparer.Ordinal);
        while (r.Read()) rows[r.GetString(0)] = r.GetString(1);
        return rows;
    }

    /// <summary>The newest business write (the drill clock), across the tables that carry one.</summary>
    public static DateTimeOffset NewestWrite(SqliteConnection db) =>
        DateTimeOffset.Parse((string)StoreSchema.Scalar(db, """
            SELECT MAX(t) FROM (SELECT MAX(last_turn_at) AS t FROM conversations
                                UNION ALL SELECT MAX(approved_at) FROM approvals
                                UNION ALL SELECT MAX(updated_at) FROM workflow_state)
            """)!, CultureInfo.InvariantCulture);

    /// <summary>The raw approvals.amount text; how to read it depends on the schema version.</summary>
    public static string? ApprovalAmountText(SqliteConnection db, string tenant, Guid operationId) =>
        StoreSchema.Scalar(db, "SELECT amount FROM approvals WHERE tenant = $t AND operation_id = $op",
            ("$t", tenant), ("$op", operationId.ToString("D"))) as string;

    public static List<Delivery> Deliveries(SqliteConnection db)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = "SELECT tenant, operation_id, employee_id, amount, currency, status, payment_id FROM deliveries ORDER BY rowid";
        using SqliteDataReader r = cmd.ExecuteReader();
        List<Delivery> rows = [];
        while (r.Read())
            rows.Add(new Delivery(r.GetString(0), Guid.Parse(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetString(4),
                Enum.Parse<DeliveryStatus>(r.GetString(5)), r.IsDBNull(6) ? null : r.GetString(6)));
        return rows;
    }
}
