using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;

namespace Ch06.SessionOwnership;

/// <summary>
/// The caller as the host's authentication produced it. The tenant and the
/// subject come from the token or the session cookie, never from the chat
/// payload, the model or a query string.
/// </summary>
public sealed record AuthenticatedCaller(string TenantId, string SubjectId);

/// <summary>The only way this code builds a store key: from the authenticated caller.</summary>
public static class ConversationKeys
{
    public const string TenantPartition = "tenant";
    public const string SubjectPartition = "subject";

    public static AgentSessionStoreKey For(AuthenticatedCaller caller, string conversationId) =>
        new AgentSessionStoreKey(conversationId)
            .WithPartition(TenantPartition, caller.TenantId)
            .WithPartition(SubjectPartition, caller.SubjectId);
}

/// <summary>Two writers saved from the same loaded version; this one lost.</summary>
public sealed class SessionConflictException(string message) : Exception(message);

/// <summary>The row version a session was loaded at: the concurrency token.</summary>
public sealed record LoadedVersion(long Value);

/// <summary>A stored row was written by a schema this code does not know.</summary>
public sealed class UnsupportedSessionSchemaException(string message) : Exception(message);

/// <summary>
/// An AgentSessionStore (experimental, MAAI001) on SQLite. One row per
/// (tenant, subject, session id): every partition is part of the identity, so
/// another subject's key finds nothing, and a delete only reaches the owner's
/// row. Each row has a schema version and a version number used as the
/// concurrency token: a save must name the version its session was loaded at.
/// </summary>
public sealed class SqliteSessionStore : AgentSessionStore
{
    public const int SchemaVersion = 1;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS sessions (
            tenant          TEXT    NOT NULL,
            subject         TEXT    NOT NULL,
            session_id      TEXT    NOT NULL,
            schema_version  INTEGER NOT NULL,
            version         INTEGER NOT NULL,
            payload         TEXT    NOT NULL,
            updated_utc     TEXT    NOT NULL,
            PRIMARY KEY (tenant, subject, session_id)
        );
        """;

    // The version a session was loaded at travels in its StateBag, so any store
    // instance (another request, another server) can check it on save. It is
    // taken out while the payload is serialized: the row's version column is
    // the only stored copy.
    public const string VersionKey = "ch06.store.version";

    public SqliteSessionStore(string databasePath)
    {
        DatabasePath = databasePath;
        using SqliteConnection db = Open();
        Execute(db, Schema);
    }

    public string DatabasePath { get; }

    public override async ValueTask<AgentSession?> GetSessionAsync(
        AIAgent agent, AgentSessionStoreKey key, CancellationToken cancellationToken = default)
    {
        (string tenant, string subject, string id) = Scope(key);
        string payload;
        long version;
        using (SqliteConnection db = Open())
        {
            using SqliteCommand cmd = Command(db, """
                SELECT schema_version, version, payload FROM sessions
                WHERE tenant = $t AND subject = $s AND session_id = $id
                """, ("$t", tenant), ("$s", subject), ("$id", id));
            using SqliteDataReader row = cmd.ExecuteReader();
            if (!row.Read()) return null;                       // not stored: never create here
            long schema = row.GetInt64(0);
            if (schema != SchemaVersion)
                throw new UnsupportedSessionSchemaException(
                    $"session {id} was stored with schema {schema}; this build reads schema {SchemaVersion}");
            version = row.GetInt64(1);
            payload = row.GetString(2);
        }

        using JsonDocument doc = JsonDocument.Parse(payload);
        AgentSession session = await agent.DeserializeSessionAsync(
            doc.RootElement.Clone(), AgentAbstractionsJsonUtilities.DefaultOptions, cancellationToken);
        session.StateBag.SetValue(VersionKey, new LoadedVersion(version), JsonSerializerOptions.Default);
        return session;                                        // a new instance on every load
    }

    public override async ValueTask SaveSessionAsync(
        AIAgent agent, AgentSessionStoreKey key, AgentSession session, CancellationToken cancellationToken = default)
    {
        (string tenant, string subject, string id) = Scope(key);
        bool loaded = session.StateBag.TryGetValue(VersionKey, out LoadedVersion? token, JsonSerializerOptions.Default);
        long loadedAt = token?.Value ?? 0;
        session.StateBag.TryRemoveValue(VersionKey);
        JsonElement state;
        try
        {
            state = await agent.SerializeSessionAsync(
                session, AgentAbstractionsJsonUtilities.DefaultOptions, cancellationToken);
        }
        finally
        {
            if (loaded) session.StateBag.SetValue(VersionKey, token, JsonSerializerOptions.Default);
        }
        string now = DateTimeOffset.UtcNow.ToString("O");
        using SqliteConnection db = Open();

        if (loaded)
        {
            // Compare-and-set in one statement: only the version we loaded may be replaced.
            int changed = Execute(db, """
                UPDATE sessions SET payload = $p, version = version + 1,
                                    schema_version = $schema, updated_utc = $u
                WHERE tenant = $t AND subject = $s AND session_id = $id AND version = $v
                """, ("$p", state.GetRawText()), ("$schema", SchemaVersion), ("$u", now),
                ("$t", tenant), ("$s", subject), ("$id", id), ("$v", loadedAt));
            if (changed == 0)
                throw new SessionConflictException(
                    $"session {id} changed or was deleted after version {loadedAt} was loaded");
            session.StateBag.SetValue(VersionKey, new LoadedVersion(loadedAt + 1), JsonSerializerOptions.Default);
            return;
        }

        try
        {
            Execute(db, """
                INSERT INTO sessions (tenant, subject, session_id, schema_version, version, payload, updated_utc)
                VALUES ($t, $s, $id, $schema, 1, $p, $u)
                """, ("$t", tenant), ("$s", subject), ("$id", id), ("$schema", SchemaVersion),
                ("$p", state.GetRawText()), ("$u", now));
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19)   // SQLITE_CONSTRAINT: the row exists
        {
            throw new SessionConflictException($"session {id} was created by another writer first");
        }
        session.StateBag.SetValue(VersionKey, new LoadedVersion(1), JsonSerializerOptions.Default);
    }

    public override ValueTask DeleteSessionAsync(
        AIAgent agent, AgentSessionStoreKey key, CancellationToken cancellationToken = default)
    {
        (string tenant, string subject, string id) = Scope(key);
        using SqliteConnection db = Open();
        // A missing row is a no-op: deleting twice succeeds twice.
        Execute(db, "DELETE FROM sessions WHERE tenant = $t AND subject = $s AND session_id = $id",
            ("$t", tenant), ("$s", subject), ("$id", id));
        return ValueTask.CompletedTask;
    }

    /// <summary>Rows in the store, for the self-test (all tenants).</summary>
    public long Count()
    {
        using SqliteConnection db = Open();
        using SqliteCommand cmd = Command(db, "SELECT COUNT(*) FROM sessions");
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Marks a row as written by another schema, for the self-test.</summary>
    internal void SetSchemaVersion(AgentSessionStoreKey key, int schemaVersion)
    {
        (string tenant, string subject, string id) = Scope(key);
        using SqliteConnection db = Open();
        Execute(db, "UPDATE sessions SET schema_version = $v WHERE tenant = $t AND subject = $s AND session_id = $id",
            ("$v", schemaVersion), ("$t", tenant), ("$s", subject), ("$id", id));
    }

    // A key without both partitions is refused: a store that fell back to the
    // session id alone would let any caller who guesses an id load the session.
    private static (string Tenant, string Subject, string Id) Scope(AgentSessionStoreKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        IReadOnlyDictionary<string, string>? p = key.Partitions;
        if (p is null || p.Count != 2 ||
            !p.TryGetValue(ConversationKeys.TenantPartition, out string? tenant) || string.IsNullOrWhiteSpace(tenant) ||
            !p.TryGetValue(ConversationKeys.SubjectPartition, out string? subject) || string.IsNullOrWhiteSpace(subject) ||
            string.IsNullOrWhiteSpace(key.SessionId))
        {
            throw new ArgumentException("a session key needs a session id and exactly the tenant and subject partitions",
                nameof(key));
        }
        return (tenant, subject, key.SessionId);
    }

    private SqliteConnection Open()
    {
        // No pooling: each call is its own physical connection, so the
        // concurrency test really has two writers.
        var cs = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false, DefaultTimeout = 30 };
        var db = new SqliteConnection(cs.ToString());
        db.Open();
        return db;
    }

    private static int Execute(SqliteConnection db, string sql, params (string Name, object Value)[] parameters)
    {
        using SqliteCommand cmd = Command(db, sql, parameters);
        return cmd.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection db, string sql, params (string Name, object Value)[] parameters)
    {
        SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }
}
