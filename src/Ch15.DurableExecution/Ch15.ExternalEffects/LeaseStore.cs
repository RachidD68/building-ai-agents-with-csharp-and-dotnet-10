using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Ch15.ExternalEffects;

/// <summary>Who may work on an item now: an owner, a fencing token that only grows, an expiry.</summary>
public sealed record Lease(string Key, string Owner, long Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Leases with fencing tokens in a SQLite table. Taking an expired lease adds
/// one to the token, so a worker that stalled past its lease holds an old
/// token, and every write that commits work repeats <see cref="FencedSql"/>
/// in its own statement: a stale holder's write changes no row.
/// </summary>
public sealed class LeaseStore
{
    /// <summary>The condition a fenced write adds to its WHERE clause (parameters $key, $token, $now).</summary>
    public const string FencedSql =
        "EXISTS (SELECT 1 FROM leases l WHERE l.lease_key = $key AND l.token = $token AND l.expires_at > $now)";

    private readonly string _path;

    public LeaseStore(string databasePath)
    {
        _path = databasePath;
        Execute("""
            CREATE TABLE IF NOT EXISTS leases (
                lease_key  TEXT PRIMARY KEY,
                owner      TEXT NOT NULL,
                token      INTEGER NOT NULL,
                expires_at TEXT NOT NULL)
            """);
    }

    /// <summary>Takes the lease if nobody holds an unexpired one; null while another owner's lease is valid.</summary>
    public Lease? TryAcquire(string key, string owner, DateTimeOffset now, TimeSpan duration)
    {
        using SqliteConnection db = Open();
        using SqliteCommand cmd = Command(db, """
            INSERT INTO leases (lease_key, owner, token, expires_at) VALUES ($key, $owner, 1, $expires)
            ON CONFLICT (lease_key) DO UPDATE SET owner = excluded.owner, token = leases.token + 1,
                                                  expires_at = excluded.expires_at
            WHERE leases.expires_at <= $now
            RETURNING token, expires_at
            """, ("$key", key), ("$owner", owner), ("$expires", Iso(now + duration)), ("$now", Iso(now)));
        using SqliteDataReader r = cmd.ExecuteReader();
        return r.Read() ? new Lease(key, owner, r.GetInt64(0), Time(r.GetString(1))) : null;
    }

    public Lease? Load(string key)
    {
        using SqliteConnection db = Open();
        using SqliteCommand cmd = Command(db, "SELECT owner, token, expires_at FROM leases WHERE lease_key = $key", ("$key", key));
        using SqliteDataReader r = cmd.ExecuteReader();
        return r.Read() ? new Lease(key, r.GetString(0), r.GetInt64(1), Time(r.GetString(2))) : null;
    }

    /// <summary>True while this token is the current, unexpired lease.</summary>
    public bool IsCurrent(string key, long token, DateTimeOffset now)
    {
        using SqliteConnection db = Open();
        using SqliteCommand cmd = Command(db, $"SELECT {FencedSql}", ("$key", key), ("$token", token), ("$now", Iso(now)));
        return (long)cmd.ExecuteScalar()! == 1;
    }

    /// <summary>Extends the lease, only while this token still holds it unexpired.</summary>
    public bool Renew(string key, long token, DateTimeOffset now, TimeSpan duration) => Execute(
        "UPDATE leases SET expires_at = $expires WHERE lease_key = $key AND token = $token AND expires_at > $now",
        ("$expires", Iso(now + duration)), ("$key", key), ("$token", token), ("$now", Iso(now))) == 1;

    /// <summary>
    /// Renews the lease every third of its duration while the process lives. A
    /// killed process stops renewing, so its lease expires; a frozen one (a long
    /// pause, a suspended VM) does too, which is what the fencing token is for.
    /// </summary>
    public IDisposable KeepAlive(string key, long token, TimeSpan duration, Func<DateTimeOffset> clock) =>
        new Timer(_ => Renew(key, token, clock(), duration), null, duration / 3, duration / 3);

    /// <summary>Gives the lease back early (the token stays, so the next owner gets token + 1). A dead worker never calls this: its lease expires.</summary>
    public void Release(string key, long token, DateTimeOffset now) => Execute(
        "UPDATE leases SET expires_at = $now WHERE lease_key = $key AND token = $token AND expires_at > $now",
        ("$now", Iso(now)), ("$key", key), ("$token", token));

    /// <summary>Timestamps as fixed-width UTC text, so SQL compares them in time order.</summary>
    public static string Iso(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static DateTimeOffset Time(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

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
            DataSource = _path,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString());
        db.Open();
        return db;
    }
}
