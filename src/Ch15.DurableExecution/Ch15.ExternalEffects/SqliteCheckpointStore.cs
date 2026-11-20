using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Data.Sqlite;

namespace Ch15.ExternalEffects;

/// <summary>
/// A checkpoint store in its own SQLite file, standing in for the shared
/// database a production checkpoint store would be. Unlike
/// FileSystemJsonCheckpointStore, which refuses a second process on the same
/// folder, several workers can open it at once, so nothing but the lease and
/// its fencing token keeps two of them from paying the same claim.
/// </summary>
public sealed class SqliteCheckpointStore : JsonCheckpointStore
{
    private readonly string _path;

    public SqliteCheckpointStore(string path)
    {
        _path = path;
        using SqliteConnection db = Open();
        using SqliteCommand create = db.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS checkpoints (
                seq           INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id    TEXT NOT NULL,
                checkpoint_id TEXT NOT NULL,
                parent_id     TEXT,
                body          TEXT NOT NULL,
                UNIQUE (session_id, checkpoint_id))
            """;
        create.ExecuteNonQuery();
    }

    public override ValueTask<CheckpointInfo> CreateCheckpointAsync(string sessionId, JsonElement value, CheckpointInfo? parent = null)
    {
        CheckpointInfo info = new(sessionId, Guid.NewGuid().ToString("N"));
        using SqliteConnection db = Open();
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO checkpoints (session_id, checkpoint_id, parent_id, body) VALUES ($s, $c, $p, $b)";
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$c", info.CheckpointId);
        cmd.Parameters.AddWithValue("$p", (object?)parent?.CheckpointId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$b", value.GetRawText());
        cmd.ExecuteNonQuery();
        return ValueTask.FromResult(info);
    }

    public override ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key)
    {
        using SqliteConnection db = Open();
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = "SELECT body FROM checkpoints WHERE session_id = $s AND checkpoint_id = $c";
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$c", key.CheckpointId);
        string body = cmd.ExecuteScalar() as string
            ?? throw new KeyNotFoundException($"no checkpoint {key.CheckpointId} in session {sessionId}");
        using JsonDocument doc = JsonDocument.Parse(body);
        return ValueTask.FromResult(doc.RootElement.Clone());
    }

    public override ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string sessionId, CheckpointInfo? withParent = null)
    {
        using SqliteConnection db = Open();
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = withParent is null
            ? "SELECT checkpoint_id FROM checkpoints WHERE session_id = $s ORDER BY seq"
            : "SELECT checkpoint_id FROM checkpoints WHERE session_id = $s AND parent_id = $p ORDER BY seq";
        cmd.Parameters.AddWithValue("$s", sessionId);
        if (withParent is not null) cmd.Parameters.AddWithValue("$p", withParent.CheckpointId);
        using SqliteDataReader r = cmd.ExecuteReader();
        List<CheckpointInfo> list = [];
        while (r.Read()) list.Add(new CheckpointInfo(sessionId, r.GetString(0)));
        return ValueTask.FromResult<IEnumerable<CheckpointInfo>>(list);
    }

    public long Count()
    {
        using SqliteConnection db = Open();
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM checkpoints";
        return (long)cmd.ExecuteScalar()!;
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
