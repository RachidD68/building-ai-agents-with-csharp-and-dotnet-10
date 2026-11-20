using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Ch24.RestoreDrill;

/// <summary>What a backup holds, written beside it and checked again at restore time.</summary>
public sealed record BackupManifest(
    string File, DateTimeOffset TakenAt, DateTimeOffset NewestWrite, int Schema,
    IReadOnlyDictionary<string, string> Identity, string IdentityDigest,
    IReadOnlyDictionary<string, long> Counts, string Sha256, string SqliteVersion);

/// <summary>A binary, with the store schemas and the embedding model it can work with.</summary>
public sealed record Release(string Version, int MinSchema, int MaxSchema, string EmbeddingModel)
{
    public override string ToString() => $"{Version} (schema {MinSchema}-{MaxSchema}, {EmbeddingModel})";
}

public sealed record RestoreResult(bool Ok, string Detail, string? Path = null);

public static class Backups
{
    /// <summary>
    /// SQLite's online backup API, through Microsoft.Data.Sqlite: it reads the
    /// database the way a connection does, WAL included, and writes a
    /// consistent copy while the service keeps writing.
    /// </summary>
    public static BackupManifest Online(ContosoStore live, string folder, DateTimeOffset takenAt)
    {
        Directory.CreateDirectory(folder);
        string file = System.IO.Path.Combine(folder, $"contoso-{takenAt:yyyyMMddTHHmm}.db");
        using (SqliteConnection source = live.Open())
        using (SqliteConnection target = new($"Data Source={file};Pooling=False"))
        {
            target.Open();
            source.BackupDatabase(target);
        }
        using SqliteConnection copy = new($"Data Source={file};Mode=ReadOnly;Pooling=False");
        copy.Open();
        Dictionary<string, string> identity = StoreFacts.Identity(copy);
        BackupManifest manifest = new(System.IO.Path.GetFileName(file), takenAt, StoreFacts.NewestWrite(copy),
            StoreSchema.Version(copy), identity, ConfigurationIdentity.Digest(identity), StoreFacts.Counts(copy),
            Sha256(file), (string)StoreSchema.Scalar(copy, "SELECT sqlite_version()")!);
        System.IO.File.WriteAllText(file + ".manifest.json", JsonSerializer.Serialize(manifest, JsonSerializerOptions.Web));
        return manifest;
    }

    /// <summary>
    /// The newest manifest in the backup folder, read from disk: at restore time
    /// the process that took the backup is gone. Its SHA-256 sits beside the
    /// backup, so it proves the file is intact, not who wrote it.
    /// </summary>
    public static BackupManifest ReadManifest(string folder)
    {
        string newest = Directory.GetFiles(folder, "contoso-*.db.manifest.json").Order(StringComparer.Ordinal).Last();
        return JsonSerializer.Deserialize<BackupManifest>(System.IO.File.ReadAllText(newest), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException($"{newest} is empty");
    }

    /// <summary>The reproduction: copy the main database file and call that a backup.</summary>
    public static string FileCopy(ContosoStore live, string folder)
    {
        Directory.CreateDirectory(folder);
        string file = System.IO.Path.Combine(folder, "contoso-filecopy.db");
        System.IO.File.Copy(live.Path, file, overwrite: true);
        return file;
    }

    public static string Sha256(string file)
    {
        using FileStream s = System.IO.File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }
}

public static class Restores
{
    /// <summary>
    /// Can this binary run against this backup? The schema must be one it
    /// reads, and the index must have been built with the embedding model it
    /// queries with. Checked before a single row is touched.
    /// </summary>
    public static string? Incompatibility(Release binary, BackupManifest m)
    {
        if (m.Schema < binary.MinSchema || m.Schema > binary.MaxSchema)
            return $"binary {binary.Version} reads schema {binary.MinSchema}-{binary.MaxSchema}; the backup is schema {m.Schema}";
        string indexModel = m.Identity["embedding_model"];
        if (indexModel != binary.EmbeddingModel)
            return $"index {m.Identity["index_version"]} was built with {indexModel}; binary {binary.Version} embeds queries with {binary.EmbeddingModel}";
        return null;
    }

    /// <summary>
    /// Restores into a folder of its own, never over the live store, and
    /// verifies the copy against the manifest before anyone uses it.
    /// </summary>
    public static RestoreResult Into(string backupFolder, BackupManifest m, Release binary, string isolatedFolder, string livePath)
    {
        string target = System.IO.Path.Combine(isolatedFolder, "contoso.db");
        if (string.Equals(System.IO.Path.GetFullPath(target), System.IO.Path.GetFullPath(livePath), StringComparison.OrdinalIgnoreCase))
            return new(false, "refused: the target is the live store");
        if (Directory.Exists(isolatedFolder) && Directory.EnumerateFileSystemEntries(isolatedFolder).Any())
            return new(false, $"refused: {System.IO.Path.GetFileName(isolatedFolder)} is not empty");
        if (Incompatibility(binary, m) is { } why) return new(false, "refused: " + why);

        string source = System.IO.Path.Combine(backupFolder, m.File);
        if (Backups.Sha256(source) != m.Sha256) return new(false, "refused: the backup file does not match its manifest");
        Directory.CreateDirectory(isolatedFolder);
        System.IO.File.Copy(source, target);

        using SqliteConnection db = new($"Data Source={target};Pooling=False");
        db.Open();
        string integrity = (string)StoreSchema.Scalar(db, "PRAGMA integrity_check")!;
        Dictionary<string, string> identity = StoreFacts.Identity(db);
        SortedDictionary<string, long> counts = StoreFacts.Counts(db);
        List<string> problems = [];
        if (integrity != "ok") problems.Add($"integrity_check: {integrity}");
        if (StoreSchema.Version(db) != m.Schema) problems.Add("schema differs from the manifest");
        if (ConfigurationIdentity.Digest(identity) != m.IdentityDigest) problems.Add("configuration identity differs");
        if (!counts.SequenceEqual(m.Counts.OrderBy(kv => kv.Key, StringComparer.Ordinal))) problems.Add("row counts differ");
        return problems.Count == 0
            ? new(true, $"integrity ok, schema {m.Schema}, identity {m.IdentityDigest}, " +
                        string.Join(" ", counts.Select(kv => $"{kv.Key}={kv.Value.ToString(CultureInfo.InvariantCulture)}")), target)
            : new(false, "refused: " + string.Join("; ", problems), target);
    }
}
