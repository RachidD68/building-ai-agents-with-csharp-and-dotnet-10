using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Ch11.AuthorizedSearch;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace Ch11.VersionedIngestion;

/// <summary>The life of one document version. Only Active is searchable.</summary>
public enum VersionState { Discovered, Validated, Indexed, Active, Superseded, Deleted }

public sealed record IngestResult(long Id, VersionState State, bool Created, string? Error = null);

/// <summary>What changed, so caches that used the old version can drop it.</summary>
public sealed record ChangeNotice(string Tenant, string DocId, int? Retired, int? Activated, string? Error = null);

/// <summary>The version that applies on a date, and whether it is still the current one.</summary>
public sealed record Applicable(int? Version, bool IsCurrent, string? Warning);

public sealed record IndexedChunk(string EvidenceId, int Version, string Text, float Score);

public sealed class IndexMismatchException(string message) : InvalidOperationException(message);

/// <summary>
/// Versioned ingestion in one SQLite file: each source version moves through
/// discovered -> validated -> indexed -> active -> superseded, or to deleted.
/// Activation is one transaction; a unique index allows one active version per
/// document; re-ingesting the same bytes finds the row it already has.
/// </summary>
public sealed class IngestionStore
{
    public const string IngestionVersion = "ingest-2";      // the pipeline's own version, stored per row
    private static readonly HashSet<string> s_tenants = ["contoso", "fabrikam"];
    private readonly string _path;

    /// <summary>Test hook: runs inside the activation transaction, before it commits.</summary>
    public Action<string>? BeforeActivationCommit { get; set; }

    public IngestionStore(string path)
    {
        _path = path;
        using SqliteConnection db = Open();
        Execute(db, null, """
            CREATE TABLE IF NOT EXISTS versions(
              id INTEGER PRIMARY KEY, tenant TEXT NOT NULL, doc_id TEXT NOT NULL, version INTEGER NOT NULL,
              content_hash TEXT NOT NULL, access INTEGER NOT NULL, effective_from TEXT NOT NULL,
              title TEXT NOT NULL, body TEXT NOT NULL, source TEXT NOT NULL, state TEXT NOT NULL,
              error TEXT, ingestion_version TEXT NOT NULL,
              UNIQUE(tenant, doc_id, content_hash), UNIQUE(tenant, doc_id, version));
            CREATE UNIQUE INDEX IF NOT EXISTS one_active ON versions(tenant, doc_id) WHERE state = 'Active';
            CREATE TABLE IF NOT EXISTS indexes(
              index_version INTEGER PRIMARY KEY, model TEXT NOT NULL, dimensions INTEGER NOT NULL,
              is_current INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS chunks(
              index_version INTEGER NOT NULL, version_id INTEGER NOT NULL, chunk INTEGER NOT NULL,
              evidence_id TEXT NOT NULL, text TEXT NOT NULL, vector BLOB NOT NULL,
              PRIMARY KEY(index_version, version_id, chunk));
            CREATE TABLE IF NOT EXISTS tombstones(
              tenant TEXT NOT NULL, doc_id TEXT NOT NULL, deleted_at TEXT NOT NULL, PRIMARY KEY(tenant, doc_id));
            """);
    }

    // ---- discovered ----------------------------------------------------------
    public IngestResult Discover(PolicyDocument doc)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{doc.Tenant}\n{doc.DocId}\n{doc.Version}\n{(int)doc.Access}\n{doc.EffectiveFrom:yyyy-MM-dd}\n{doc.Title}\n{doc.Body}")));
        using SqliteConnection db = Open();
        // One write transaction from the first read: the tombstone check and the insert
        // cannot interleave with a delete, and no reader has to upgrade to a writer.
        using SqliteTransaction tx = db.BeginTransaction(deferred: false);
        if (Scalar(db, tx, "SELECT 1 FROM tombstones WHERE tenant = $t AND doc_id = $d", ("$t", doc.Tenant), ("$d", doc.DocId)) is not null)
            return new IngestResult(0, VersionState.Deleted, false, "deleted");
        try
        {
            int inserted = Execute(db, tx, """
                INSERT INTO versions(tenant, doc_id, version, content_hash, access, effective_from, title, body,
                                     source, state, ingestion_version)
                VALUES($t, $d, $v, $h, $a, $e, $ti, $b, $s, 'Discovered', $iv)
                ON CONFLICT(tenant, doc_id, content_hash) DO NOTHING
                """,
                ("$t", doc.Tenant), ("$d", doc.DocId), ("$v", doc.Version), ("$h", hash), ("$a", (int)doc.Access),
                ("$e", doc.EffectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("$ti", doc.Title), ("$b", doc.Body), ("$s", doc.SourcePath), ("$iv", IngestionVersion));
            (long id, VersionState state) = Row(db, tx, "tenant = $t AND doc_id = $d AND content_hash = $h",
                ("$t", doc.Tenant), ("$d", doc.DocId), ("$h", hash));
            tx.Commit();
            return new IngestResult(id, state, inserted == 1);
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19)       // same version number, other bytes
        {
            return new IngestResult(0, VersionState.Discovered, false, "version_conflict");
        }
    }

    // ---- validated -----------------------------------------------------------
    public IngestResult Validate(long id)
    {
        using SqliteConnection db = Open();
        using SqliteTransaction tx = db.BeginTransaction(deferred: false);
        string? error;
        using (SqliteCommand read = Command(db, tx, "SELECT tenant, title, body FROM versions WHERE id = $id", ("$id", id)))
        using (SqliteDataReader r = read.ExecuteReader())
        {
            if (!r.Read()) return new IngestResult(id, VersionState.Discovered, false, "unknown_version");
            error = !s_tenants.Contains(r.GetString(0)) ? "unknown_tenant"
                : r.GetString(1).Length == 0 || r.GetString(2).Length == 0 ? "empty_document"
                : null;
        }                                                   // the reader is closed before the write
        Execute(db, tx, "UPDATE versions SET state = CASE WHEN $err IS NULL THEN 'Validated' ELSE state END, error = $err " +
                        "WHERE id = $id AND state = 'Discovered'", ("$id", id), ("$err", (object?)error ?? DBNull.Value));
        VersionState state = Row(db, tx, "id = $id", ("$id", id)).State;
        tx.Commit();
        return new IngestResult(id, state, false, error);
    }

    // ---- indexed -------------------------------------------------------------
    public async Task<IngestResult> IndexAsync(long id, IEmbeddingGenerator<string, Embedding<float>> embedder,
        string model, int? indexVersion = null, CancellationToken ct = default)
    {
        using SqliteConnection db = Open();
        int index = indexVersion ?? CurrentIndex(db).Version;
        string indexModel = (string)Scalar(db, null, "SELECT model FROM indexes WHERE index_version = $i", ("$i", index))!;
        if (indexModel != model)
            throw new IndexMismatchException($"index {index} is built with {indexModel}, not {model}");

        (string docId, int version, string body, VersionState state) = ReadVersion(db, id);
        if (state is VersionState.Discovered or VersionState.Deleted)
            return new IngestResult(id, state, false, "not_validated");
        string[] paragraphs = [.. new PolicyDocument(docId, version, "", 0, 0, default, "", body, "").Paragraphs()];
        GeneratedEmbeddings<Embedding<float>> vectors = await embedder.GenerateAsync(paragraphs, cancellationToken: ct);

        using SqliteTransaction tx = db.BeginTransaction(deferred: false);
        // Embedding ran outside the lock: a delete may have landed meanwhile. Re-read under it.
        string tenant = (string)Scalar(db, tx, "SELECT tenant FROM versions WHERE id = $id", ("$id", id))!;
        if (ReadVersion(db, id, tx).State == VersionState.Deleted
            || Scalar(db, tx, "SELECT 1 FROM tombstones WHERE tenant = $t AND doc_id = $d", ("$t", tenant), ("$d", docId)) is not null)
            return new IngestResult(id, VersionState.Deleted, false, "deleted");      // disposing rolls back
        for (int i = 0; i < paragraphs.Length; i++)
        {
            Execute(db, tx, "INSERT OR REPLACE INTO chunks VALUES($i, $v, $c, $e, $t, $vec)",
                ("$i", index), ("$v", id), ("$c", i), ("$e", $"{docId}@v{version}#{i}"), ("$t", paragraphs[i]),
                ("$vec", MemoryMarshal.AsBytes(vectors[i].Vector.Span).ToArray()));
        }
        Execute(db, tx, "UPDATE versions SET state = 'Indexed' WHERE id = $id AND state = 'Validated'", ("$id", id));
        tx.Commit();
        return new IngestResult(id, Row(db, "id = $id", ("$id", id)).State, false);
    }

    // ---- active / superseded: one transaction ----------------------------------
    public ChangeNotice Activate(long id, DateOnly today)
    {
        using SqliteConnection db = Open();
        using SqliteTransaction tx = db.BeginTransaction(deferred: false);   // BEGIN IMMEDIATE: one writer
        (string docId, int version, _, VersionState state) = ReadVersion(db, id, tx);
        string tenant = (string)Scalar(db, tx, "SELECT tenant FROM versions WHERE id = $id", ("$id", id))!;
        if (state == VersionState.Active) return new ChangeNotice(tenant, docId, null, null, "already_active");
        if (state != VersionState.Indexed) return new ChangeNotice(tenant, docId, null, null, "not_indexed");
        string effective = (string)Scalar(db, tx, "SELECT effective_from FROM versions WHERE id = $id", ("$id", id))!;
        if (DateOnly.ParseExact(effective, "yyyy-MM-dd", CultureInfo.InvariantCulture) > today)
            return new ChangeNotice(tenant, docId, null, null, "not_yet_effective");      // stays Indexed
        object? active = Scalar(db, tx, "SELECT version FROM versions WHERE tenant = $t AND doc_id = $d AND state = 'Active'",
            ("$t", tenant), ("$d", docId));
        int? retired = active is null ? null : Convert.ToInt32(active, CultureInfo.InvariantCulture);
        if (retired > version)                    // a late retry of an old version
        {
            Execute(db, tx, "UPDATE versions SET state = 'Superseded' WHERE id = $id", ("$id", id));
            tx.Commit();
            return new ChangeNotice(tenant, docId, null, null, "older_than_active");
        }
        Execute(db, tx, "UPDATE versions SET state = 'Superseded' WHERE tenant = $t AND doc_id = $d AND state = 'Active'",
            ("$t", tenant), ("$d", docId));
        Execute(db, tx, "UPDATE versions SET state = 'Active' WHERE id = $id", ("$id", id));
        BeforeActivationCommit?.Invoke($"{docId}@v{version}");
        tx.Commit();
        return new ChangeNotice(tenant, docId, retired, version);
    }

    // ---- deleted: tombstone, chunks gone from every index -----------------------
    public ChangeNotice Delete(string tenant, string docId, DateTimeOffset now)
    {
        using SqliteConnection db = Open();
        using SqliteTransaction tx = db.BeginTransaction(deferred: false);
        object? active = Scalar(db, tx, "SELECT version FROM versions WHERE tenant = $t AND doc_id = $d AND state = 'Active'",
            ("$t", tenant), ("$d", docId));
        Execute(db, tx, "DELETE FROM chunks WHERE version_id IN (SELECT id FROM versions WHERE tenant = $t AND doc_id = $d)",
            ("$t", tenant), ("$d", docId));
        Execute(db, tx, "UPDATE versions SET state = 'Deleted', body = '' WHERE tenant = $t AND doc_id = $d",
            ("$t", tenant), ("$d", docId));
        Execute(db, tx, "INSERT OR IGNORE INTO tombstones VALUES($t, $d, $at)",
            ("$t", tenant), ("$d", docId), ("$at", now.ToString("O", CultureInfo.InvariantCulture)));
        tx.Commit();
        return new ChangeNotice(tenant, docId, active is null ? null : Convert.ToInt32(active, CultureInfo.InvariantCulture), null);
    }

    /// <summary>
    /// Applicability is the effective date, not the activation: the latest version in force on that day.
    /// Activate refuses a version before its effective date, so no Active or Superseded row lies in the future.
    /// </summary>
    public Applicable ApplicableOn(string tenant, string docId, DateOnly day)
    {
        using SqliteConnection db = Open();
        string d = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using SqliteCommand cmd = Command(db, null, """
            SELECT version, state, (SELECT MIN(effective_from) FROM versions n
                                    WHERE n.tenant = v.tenant AND n.doc_id = v.doc_id AND n.state IN ('Active','Superseded')
                                      AND n.effective_from > v.effective_from)
            FROM versions v
            WHERE tenant = $t AND doc_id = $d AND state IN ('Active','Superseded') AND effective_from <= $day
            ORDER BY effective_from DESC LIMIT 1
            """, ("$t", tenant), ("$d", docId), ("$day", d));
        using SqliteDataReader r = cmd.ExecuteReader();
        if (!r.Read())
            return new Applicable(null, false, Scalar(db, null, "SELECT 1 FROM tombstones WHERE tenant = $t AND doc_id = $d",
                ("$t", tenant), ("$d", docId)) is null ? "no_version_in_force" : "deleted");
        int version = r.GetInt32(0);
        bool current = r.GetString(1) == "Active";
        string? replacedOn = r.IsDBNull(2) ? null : r.GetString(2);
        return new Applicable(version, current && replacedOn is null,
            replacedOn is null ? (current ? null : "superseded") : $"not current: replaced from {replacedOn}");
    }

    // ---- index versions: a new embedding model is a new index --------------------
    public int CreateIndex(string model, int dimensions, bool makeCurrent)
    {
        using SqliteConnection db = Open();
        using SqliteTransaction tx = db.BeginTransaction(deferred: false);
        int next = Convert.ToInt32(Scalar(db, tx, "SELECT COALESCE(MAX(index_version), 0) + 1 FROM indexes")!, CultureInfo.InvariantCulture);
        Execute(db, tx, "INSERT INTO indexes VALUES($i, $m, $d, 0)", ("$i", next), ("$m", model), ("$d", dimensions));
        if (makeCurrent) SwitchIndex(db, tx, next);
        tx.Commit();
        return next;
    }

    /// <summary>Builds an index version for every active and indexed version; switch to it when it measures well.</summary>
    public async Task BuildIndexAsync(int index, IEmbeddingGenerator<string, Embedding<float>> embedder, string model, CancellationToken ct = default)
    {
        List<long> ids = [];
        using (SqliteConnection db = Open())
        using (SqliteCommand cmd = Command(db, null, "SELECT id FROM versions WHERE state IN ('Indexed','Active')"))
        using (SqliteDataReader r = cmd.ExecuteReader())
        {
            while (r.Read()) ids.Add(r.GetInt64(0));
        }
        foreach (long id in ids) await IndexAsync(id, embedder, model, index, ct);
    }

    public void SwitchIndex(int index)
    {
        using SqliteConnection db = Open();
        using SqliteTransaction tx = db.BeginTransaction(deferred: false);
        SwitchIndex(db, tx, index);
        tx.Commit();
    }

    public (int Version, string Model) CurrentIndex()
    {
        using SqliteConnection db = Open();
        return CurrentIndex(db);
    }

    /// <summary>Searches active chunks of one tenant in the current index; refuses a query vector from another model.</summary>
    public IReadOnlyList<IndexedChunk> Search(string tenant, ReadOnlyMemory<float> query, string queryModel, int top = 3)
    {
        using SqliteConnection db = Open();
        (int index, string model) = CurrentIndex(db);
        if (model != queryModel)
            throw new IndexMismatchException($"the current index {index} holds {model} vectors; the query was embedded with {queryModel}");
        long dimensions = Convert.ToInt64(Scalar(db, null, "SELECT dimensions FROM indexes WHERE index_version = $i", ("$i", index))!,
            CultureInfo.InvariantCulture);
        if (query.Length != dimensions)        // the label is the caller's word; the length is checked too
            throw new IndexMismatchException($"the current index {index} holds {dimensions}-float vectors; the query has {query.Length}");
        using SqliteCommand cmd = Command(db, null, """
            SELECT c.evidence_id, v.version, c.text, c.vector FROM chunks c JOIN versions v ON v.id = c.version_id
            WHERE c.index_version = $i AND v.tenant = $t AND v.state = 'Active'
            """, ("$i", index), ("$t", tenant));
        using SqliteDataReader r = cmd.ExecuteReader();
        List<IndexedChunk> hits = [];
        while (r.Read())
        {
            float[] vector = MemoryMarshal.Cast<byte, float>((byte[])r.GetValue(3)).ToArray();
            hits.Add(new IndexedChunk(r.GetString(0), r.GetInt32(1), r.GetString(2), Cosine(query.Span, vector)));
        }
        return [.. hits.OrderByDescending(h => h.Score).ThenBy(h => h.EvidenceId, StringComparer.Ordinal).Take(top)];
    }

    public IReadOnlyList<(int Version, VersionState State)> Versions(string tenant, string docId)
    {
        using SqliteConnection db = Open();
        using SqliteCommand cmd = Command(db, null, "SELECT version, state FROM versions WHERE tenant = $t AND doc_id = $d ORDER BY version",
            ("$t", tenant), ("$d", docId));
        using SqliteDataReader r = cmd.ExecuteReader();
        List<(int, VersionState)> rows = [];
        while (r.Read()) rows.Add((r.GetInt32(0), Enum.Parse<VersionState>(r.GetString(1))));
        return rows;
    }

    /// <summary>The current state and access level of one version, for a delivery-time check.</summary>
    public (VersionState State, AccessLevel Access)? Find(string tenant, string docId, int version)
    {
        using SqliteConnection db = Open();
        using SqliteCommand cmd = Command(db, null,
            "SELECT state, access FROM versions WHERE tenant = $t AND doc_id = $d AND version = $v",
            ("$t", tenant), ("$d", docId), ("$v", version));
        using SqliteDataReader r = cmd.ExecuteReader();
        return r.Read() ? (Enum.Parse<VersionState>(r.GetString(0)), (AccessLevel)r.GetInt32(1)) : null;
    }

    public long Count(string sql, params (string Name, object Value)[] parameters)
    {
        using SqliteConnection db = Open();
        return Convert.ToInt64(Scalar(db, null, sql, parameters)!, CultureInfo.InvariantCulture);
    }

    public static float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na == 0 || nb == 0 ? 0 : dot / (MathF.Sqrt(na) * MathF.Sqrt(nb));
    }

    // ---- plumbing --------------------------------------------------------------
    private static void SwitchIndex(SqliteConnection db, SqliteTransaction tx, int index)
    {
        Execute(db, tx, "UPDATE indexes SET is_current = CASE WHEN index_version = $i THEN 1 ELSE 0 END", ("$i", index));
    }

    private static (int Version, string Model) CurrentIndex(SqliteConnection db)
    {
        using SqliteCommand cmd = Command(db, null, "SELECT index_version, model FROM indexes WHERE is_current = 1");
        using SqliteDataReader r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt32(0), r.GetString(1)) : throw new InvalidOperationException("no current index");
    }

    private static (string DocId, int Version, string Body, VersionState State) ReadVersion(
        SqliteConnection db, long id, SqliteTransaction? tx = null)
    {
        using SqliteCommand cmd = Command(db, tx, "SELECT doc_id, version, body, state FROM versions WHERE id = $id", ("$id", id));
        using SqliteDataReader r = cmd.ExecuteReader();
        return r.Read()
            ? (r.GetString(0), r.GetInt32(1), r.GetString(2), Enum.Parse<VersionState>(r.GetString(3)))
            : throw new InvalidOperationException($"no version row {id}");
    }

    private static (long Id, VersionState State) Row(SqliteConnection db, string where, params (string Name, object Value)[] p) =>
        Row(db, null, where, p);

    private static (long Id, VersionState State) Row(SqliteConnection db, SqliteTransaction? tx, string where,
        params (string Name, object Value)[] p)
    {
        using SqliteCommand cmd = Command(db, tx, $"SELECT id, state FROM versions WHERE {where}", p);
        using SqliteDataReader r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt64(0), Enum.Parse<VersionState>(r.GetString(1))) : (0, VersionState.Discovered);
    }

    private SqliteConnection Open()
    {
        // No pooling: each Open() is its own connection, so the concurrency tests
        // really are separate writers waiting on SQLite's lock.
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path, Pooling = false, DefaultTimeout = 30,
        }.ToString());
        db.Open();
        return db;
    }

    private static int Execute(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object Value)[] p)
    {
        using SqliteCommand cmd = Command(db, tx, sql, p);
        return cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object Value)[] p)
    {
        using SqliteCommand cmd = Command(db, tx, sql, p);
        return cmd.ExecuteScalar();
    }

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object Value)[] p)
    {
        SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach ((string name, object value) in p) cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }
}
