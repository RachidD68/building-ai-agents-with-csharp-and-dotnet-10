using System.Security.Cryptography;
using System.Text;
using Ch11.AuthorizedSearch;

namespace Ch11.ScopedCache;

/// <summary>Who an entry may be served to: the authorization scope, never the person.</summary>
public sealed record CacheScope(string Tenant, AccessLevel Level)
{
    public static CacheScope Of(CallerContext caller) => new(caller.Tenant, caller.Level);
}

/// <summary>A document version an entry was built from.</summary>
public sealed record Dependency(string Tenant, string DocId, int Version)
{
    public static Dependency Of(EvidenceRecord e) => new(e.Tenant, e.DocId, e.Version);
}

public sealed record CacheLookup(bool Hit, string? Value, string Reason);

/// <summary>
/// Two caches (retrieved chunks, generated answers) with one dependency index.
/// Keys are HMACs of the scope and every input that changes the result, so they
/// carry no question text and no subject. Revoking a document removes every
/// entry built from it at once, a TTL only bounds how old an entry may get, and
/// a write computed before a revocation in its tenant is refused.
/// </summary>
public sealed class ScopedCache(TimeProvider clock, TimeSpan ttl, byte[] keySecret)
{
    private sealed record Entry(string Value, IReadOnlyList<Dependency> Dependencies, CacheScope Scope, DateTimeOffset ExpiresAt);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Tenant, string DocId), HashSet<string>> _dependents = [];
    private readonly Dictionary<string, long> _epochs = new(StringComparer.Ordinal);

    public string AnswerKey(CacheScope scope, string question, string corpusVersion, string model, string promptVersion) =>
        Key("answer", scope.Tenant, scope.Level.ToString(), Normalize(question), corpusVersion, model, promptVersion);

    public string ChunkKey(CacheScope scope, string query, string indexVersion) =>
        Key("chunks", scope.Tenant, scope.Level.ToString(), Normalize(query), indexVersion);

    /// <summary>Read before retrieval; a write carrying an older token is refused.</summary>
    public long BeginCompute(string tenant)
    {
        lock (_gate) return _epochs.GetValueOrDefault(tenant);
    }

    public bool TryPut(string key, string value, CacheScope scope, IEnumerable<Dependency> dependencies, long token)
    {
        lock (_gate)
        {
            if (_epochs.GetValueOrDefault(scope.Tenant) != token) return false;   // revoked while computing
            Dependency[] deps = [.. dependencies.Distinct()];
            if (deps.Any(d => d.Tenant != scope.Tenant)) return false;          // never mix tenants in one entry
            Remove(key);
            _entries[key] = new Entry(value, deps, scope, clock.GetUtcNow() + ttl);
            foreach (Dependency d in deps)
            {
                if (!_dependents.TryGetValue((d.Tenant, d.DocId), out HashSet<string>? keys))
                    _dependents[(d.Tenant, d.DocId)] = keys = new HashSet<string>(StringComparer.Ordinal);
                keys.Add(key);
            }
            return true;
        }
    }

    /// <summary>
    /// Serves an entry only if it has not expired and every document it was built
    /// from is still readable by this scope under the current policy.
    /// </summary>
    public CacheLookup TryGet(string key, Func<CacheScope, Dependency, bool> stillReadable)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out Entry? entry)) return new CacheLookup(false, null, "miss");
            if (clock.GetUtcNow() >= entry.ExpiresAt)
            {
                Remove(key);
                return new CacheLookup(false, null, "expired");
            }
            if (!entry.Dependencies.All(d => stillReadable(entry.Scope, d)))
            {
                Remove(key);
                return new CacheLookup(false, null, "dependency_not_readable");
            }
            return new CacheLookup(true, entry.Value, "hit");
        }
    }

    /// <summary>Removes every entry, in both caches, built from any version of the document.</summary>
    public int Revoke(string tenant, string docId)
    {
        lock (_gate)
        {
            _epochs[tenant] = _epochs.GetValueOrDefault(tenant) + 1;
            if (!_dependents.Remove((tenant, docId), out HashSet<string>? keys)) return 0;
            int removed = 0;
            foreach (string key in keys) removed += Remove(key) ? 1 : 0;
            return removed;
        }
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public int CountFor(string tenant)
    {
        lock (_gate) return _entries.Values.Count(e => e.Scope.Tenant == tenant);
    }

    private bool Remove(string key)
    {
        if (!_entries.Remove(key, out Entry? entry)) return false;
        foreach (Dependency d in entry.Dependencies)
        {
            if (_dependents.TryGetValue((d.Tenant, d.DocId), out HashSet<string>? keys)) keys.Remove(key);
        }
        return true;
    }

    private string Key(params string[] parts)
    {
        // Unit separator between fields, so ("a b", "c") and ("a", "b c") differ.
        byte[] canonical = Encoding.UTF8.GetBytes(string.Join('\u001f', parts));
        return $"{parts[0]}:{Convert.ToHexStringLower(HMACSHA256.HashData(keySecret, canonical))}";
    }

    private static string Normalize(string text) =>
        string.Join(' ', text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
