// ============================================================================
// Versioned ingestion: a knowledge base that changes without lying
// ----------------------------------------------------------------------------
// Each source version moves through discovered -> validated -> indexed ->
// active -> superseded, or to deleted (a tombstone). Only an active version is
// searchable. Activation is one transaction, so a reader sees the old version
// or the new one, never neither and never both. Re-ingesting the same bytes
// finds the row it already has. A date asks which version applied on that day.
// A new embedding model is a new index version, built beside the old one.
//
// No model and no network: an offline hashing embedder stands in for two
// embedding models, and the store is one SQLite file in a temporary folder.
//   dotnet run                    runs the scenario and prints it
//   dotnet run -- --self-test     the same, as a test: RESULT n/n, exit code 1 on failure
// ============================================================================

using Ch11.AuthorizedSearch;
using Ch11.VersionedIngestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

bool selfTest = args.Contains("--self-test");
Console.WriteLine(selfTest
    ? "=== Versioned ingestion: self-test ==="
    : "=== Versioned ingestion (no model; the same suite runs with --self-test) ===");

string root = Path.Combine(Path.GetTempPath(), $"ch11-ingestion-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
try
{
    return await Suite.RunAsync(root);
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(root, recursive: true);
}

internal static class Suite
{
    static readonly HashingEmbedder ModelA = new(seed: 0);
    static readonly HashingEmbedder ModelB = new(seed: 7);
    static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
    static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);
    static int s_passed, s_total;

    public static async Task<int> RunAsync(string root)
    {
        Dictionary<string, PolicyDocument> corpus = Corpus.Load().ToDictionary(d => $"{d.DocId}@v{d.Version}");
        var store = new IngestionStore(Path.Combine(root, "ingestion.db"));
        store.CreateIndex(ModelA.ModelId, HashingEmbedder.Dimensions, makeCurrent: true);

        // ---- Lifecycle -----------------------------------------------------------
        Console.WriteLine("\n-- Lifecycle of one document --");
        PolicyDocument taxi1 = corpus["contoso-taxi@v1"], taxi2 = corpus["contoso-taxi@v2"], draft = corpus["contoso-taxi@v3"];
        IngestResult d1 = store.Discover(taxi1);
        IngestResult v1 = store.Validate(d1.Id);
        IngestResult i1 = await store.IndexAsync(d1.Id, ModelA, ModelA.ModelId);
        bool searchableBefore = Search(store, "contoso", "taxi receipt").Any(h => h.EvidenceId.StartsWith("contoso-taxi", StringComparison.Ordinal));
        ChangeNotice a1 = store.Activate(d1.Id, Today);
        Expect($"v1: {d1.State} -> {v1.State} -> {i1.State} (searchable: {searchableBefore}) -> {State(store, "contoso-taxi", 1)}",
            d1.Created && v1.State == VersionState.Validated && i1.State == VersionState.Indexed && !searchableBefore
            && a1.Activated == 1 && State(store, "contoso-taxi", 1) == VersionState.Active);

        ChangeNotice a2 = await IngestAsync(store, taxi2);
        Expect($"v2 activated: notice retires v{a2.Retired}, activates v{a2.Activated}; v1 is {State(store, "contoso-taxi", 1)}",
            a2 is { Retired: 1, Activated: 2 } && State(store, "contoso-taxi", 1) == VersionState.Superseded);
        Expect("search returns v2 chunks only",
            Search(store, "contoso", "taxi receipt claim").Where(h => h.EvidenceId.StartsWith("contoso-taxi", StringComparison.Ordinal))
                .All(h => h.Version == 2));

        IngestResult dd = store.Discover(draft);
        store.Validate(dd.Id);
        await store.IndexAsync(dd.Id, ModelA, ModelA.ModelId);
        Expect($"the management draft is indexed, not activated: {State(store, "contoso-taxi", 3)}, never in search",
            State(store, "contoso-taxi", 3) == VersionState.Indexed
            && Search(store, "contoso", "draft manager approval taxi", 10).All(h => h.Version != 3));
        ChangeNotice early = store.Activate(dd.Id, new DateOnly(2026, 11, 20));
        Expect($"activating it on 20 November, before its effective date 2027-01-01 -> {early.Error}; it stays {State(store, "contoso-taxi", 3)}, v2 stays {State(store, "contoso-taxi", 2)}",
            early.Error == "not_yet_effective" && State(store, "contoso-taxi", 3) == VersionState.Indexed
            && State(store, "contoso-taxi", 2) == VersionState.Active);

        // ---- Idempotence and conflicts -----------------------------------------------
        Console.WriteLine("\n-- Re-ingestion, conflicts and concurrency --");
        long chunksBefore = store.Count("SELECT COUNT(*) FROM chunks");
        IngestResult again = store.Discover(taxi2);
        Expect($"same bytes again: created {again.Created}, state {again.State}; one active version, chunk count unchanged",
            !again.Created && again.State == VersionState.Active && ActiveCount(store, "contoso-taxi") == 1
            && store.Count("SELECT COUNT(*) FROM chunks") == chunksBefore);

        IngestResult conflict = store.Discover(taxi2 with { Body = taxi2.Body + " Changed." });
        Expect($"same version number, other bytes -> {conflict.Error}", conflict.Error == "version_conflict");

        PolicyDocument hotel = corpus["contoso-hotel@v1"];
        Task<ChangeNotice>[] racers = [.. Enumerable.Range(0, 8).Select(_ => Task.Run(() => IngestAsync(store, hotel)))];
        await Task.WhenAll(racers);
        Expect($"8 concurrent ingestions of one source: {store.Count("SELECT COUNT(*) FROM versions WHERE doc_id = 'contoso-hotel'")} row, " +
               $"{ActiveCount(store, "contoso-hotel")} active, {racers.Count(r => r.Result.Activated == 1)} activation",
            store.Count("SELECT COUNT(*) FROM versions WHERE doc_id = 'contoso-hotel'") == 1
            && ActiveCount(store, "contoso-hotel") == 1 && racers.Count(r => r.Result.Activated == 1) == 1);

        PolicyDocument fab1 = corpus["fabrikam-taxi@v1"], fab2 = corpus["fabrikam-taxi@v2"];
        long f1 = await PrepareAsync(store, fab1), f2 = await PrepareAsync(store, fab2);
        Task<ChangeNotice> t1 = Task.Run(() => store.Activate(f1, Today)), t2 = Task.Run(() => store.Activate(f2, Today));
        await Task.WhenAll(t1, t2);
        Expect($"v1 and v2 activated at the same time: v2 {State(store, "fabrikam-taxi", 2)}, v1 {State(store, "fabrikam-taxi", 1)}" +
               $" ({t1.Result.Error ?? "activated"} / {t2.Result.Error ?? "activated"})",
            State(store, "fabrikam-taxi", 2) == VersionState.Active && State(store, "fabrikam-taxi", 1) == VersionState.Superseded);

        PolicyDocument hotel2 = hotel with { Version = 2, Body = hotel.Body.Replace("140.00", "150.00", StringComparison.Ordinal) };
        await IngestAsync(store, hotel2);
        ChangeNotice late = await IngestAsync(store, hotel with { Version = 0, Body = "An older hotel rule, retried late." });
        Expect($"a late retry of an older version after v2 is active -> {late.Error}; v2 stays active",
            late.Error == "older_than_active" && State(store, "contoso-hotel", 2) == VersionState.Active);

        IngestResult bad = store.Discover(hotel with { Tenant = "northwind", DocId = "stray" });
        IngestResult badValidated = store.Validate(bad.Id);
        Expect($"unknown tenant -> {badValidated.Error}; it stays {badValidated.State}, activation -> {store.Activate(bad.Id, Today).Error}",
            badValidated is { Error: "unknown_tenant", State: VersionState.Discovered } && store.Activate(bad.Id, Today).Error == "not_indexed");

        // ---- Atomic activation ---------------------------------------------------
        Console.WriteLine("\n-- The activation boundary --");
        var naiveStore = new IngestionStore(Path.Combine(root, "naive.db"));
        naiveStore.CreateIndex(ModelA.ModelId, HashingEmbedder.Dimensions, makeCurrent: true);
        await IngestAsync(naiveStore, taxi1);
        long n2 = await PrepareAsync(naiveStore, taxi2);
        try { NaiveActivate(Path.Combine(root, "naive.db"), "contoso", "contoso-taxi", n2, crashBetween: true); }
        catch (InvalidOperationException) { }
        Expect($"DEFECT REPRODUCED: two separate updates, crash in between: {ActiveCount(naiveStore, "contoso-taxi")} active taxi versions, " +
               $"{Search(naiveStore, "contoso", "taxi receipt").Count} search results",
            ActiveCount(naiveStore, "contoso-taxi") == 0 && Search(naiveStore, "contoso", "taxi receipt").Count == 0);

        var atomicStore = new IngestionStore(Path.Combine(root, "atomic.db"));
        atomicStore.CreateIndex(ModelA.ModelId, HashingEmbedder.Dimensions, makeCurrent: true);
        await IngestAsync(atomicStore, taxi1);
        long m2 = await PrepareAsync(atomicStore, taxi2);
        atomicStore.BeforeActivationCommit = step => throw new InvalidOperationException($"simulated crash before committing {step}");
        try { atomicStore.Activate(m2, Today); } catch (InvalidOperationException) { }
        atomicStore.BeforeActivationCommit = null;
        Expect($"one transaction, same crash: v1 {State(atomicStore, "contoso-taxi", 1)}, v2 {State(atomicStore, "contoso-taxi", 2)}; search still answers from v1",
            State(atomicStore, "contoso-taxi", 1) == VersionState.Active && State(atomicStore, "contoso-taxi", 2) == VersionState.Indexed
            && Search(atomicStore, "contoso", "taxi receipt").Any(h => h.EvidenceId.StartsWith("contoso-taxi@v1", StringComparison.Ordinal)));
        ChangeNotice retry = atomicStore.Activate(m2, Today);
        Expect($"the retry after restart activates v2 (retires v{retry.Retired})", retry is { Retired: 1, Activated: 2 });

        // ---- Effective dates -----------------------------------------------------
        Console.WriteLine("\n-- Which version applied on a date --");
        Applicable before = store.ApplicableOn("contoso", "contoso-taxi", new DateOnly(2026, 11, 10));
        Applicable after = store.ApplicableOn("contoso", "contoso-taxi", new DateOnly(2026, 11, 20));
        Applicable none = store.ApplicableOn("contoso", "contoso-taxi", new DateOnly(2024, 6, 1));
        Expect($"a ride on 10 November: v{before.Version}, current {before.IsCurrent}, warning \"{before.Warning}\"",
            before is { Version: 1, IsCurrent: false } && before.Warning!.Contains("2026-11-16", StringComparison.Ordinal));
        Expect($"a ride on 20 November: v{after.Version}, current {after.IsCurrent}, no warning",
            after is { Version: 2, IsCurrent: true, Warning: null });
        Expect($"a ride in June 2024: no version in force -> {none.Warning}", none is { Version: null, Warning: "no_version_in_force" });

        // ---- Deletion ------------------------------------------------------------
        Console.WriteLine("\n-- Deletion: tombstone and every index --");
        PolicyDocument relocation = corpus["contoso-relocation@v1"] with { State = SourceState.Active };
        await IngestAsync(store, relocation);
        bool foundBefore = Search(store, "contoso", "relocation allowance", 5).Any(h => h.EvidenceId.StartsWith("contoso-relocation", StringComparison.Ordinal));
        ChangeNotice deleted = store.Delete("contoso", "contoso-relocation", Now);
        Expect($"delete: found before {foundBefore}; notice retires v{deleted.Retired}; chunks left {store.Count("SELECT COUNT(*) FROM chunks c JOIN versions v ON v.id = c.version_id WHERE v.doc_id = 'contoso-relocation'")}; " +
               $"on a date -> {store.ApplicableOn("contoso", "contoso-relocation", new DateOnly(2026, 11, 20)).Warning}",
            foundBefore && deleted.Retired == 1
            && store.Count("SELECT COUNT(*) FROM chunks c JOIN versions v ON v.id = c.version_id WHERE v.doc_id = 'contoso-relocation'") == 0
            && store.ApplicableOn("contoso", "contoso-relocation", new DateOnly(2026, 11, 20)).Warning == "deleted");
        Expect($"the same bytes ingested again -> {store.Discover(relocation).Error}: a retry does not resurrect it",
            store.Discover(relocation).Error == "deleted");

        // A delete that lands while the chunks are being embedded, outside the write lock.
        PolicyDocument receipt = corpus["contoso-lost-receipt@v1"];
        IngestResult r1 = store.Discover(receipt);
        store.Validate(r1.Id);
        var deleting = new DeleteWhileEmbedding(ModelA, () => store.Delete("contoso", "contoso-lost-receipt", Now));
        IngestResult racedIndex = await store.IndexAsync(r1.Id, deleting, ModelA.ModelId);
        long receiptChunks = store.Count("SELECT COUNT(*) FROM chunks c JOIN versions v ON v.id = c.version_id WHERE v.doc_id = 'contoso-lost-receipt'");
        Expect($"delete during indexing: index -> {racedIndex.Error}, state {State(store, "contoso-lost-receipt", 1)}, chunks left {receiptChunks}",
            racedIndex.Error == "deleted" && State(store, "contoso-lost-receipt", 1) == VersionState.Deleted && receiptChunks == 0);

        // ---- Embedding migration -------------------------------------------------
        Console.WriteLine("\n-- A new embedding model is a new index version --");
        string text = taxi2.Paragraphs()[0];
        float[] va = ModelA.Embed(text), vb = ModelB.Embed(text);
        float cross = IngestionStore.Cosine(va, vb);
        Expect($"DEFECT REPRODUCED: same text, two models, both {va.Length} dimensions: cosine {cross:0.00} (same model: {IngestionStore.Cosine(va, ModelA.Embed(text)):0.00})",
            va.Length == vb.Length && cross < 0.5f);
        bool refused = false;
        try { store.Search("contoso", vb, ModelB.ModelId); } catch (IndexMismatchException) { refused = true; }
        Expect("a query from the new model against the old index is refused", refused);

        int index2 = store.CreateIndex(ModelB.ModelId, HashingEmbedder.Dimensions, makeCurrent: false);
        await store.BuildIndexAsync(index2, ModelB, ModelB.ModelId);
        store.SwitchIndex(index2);
        IReadOnlyList<IndexedChunk> migrated = store.Search("contoso", ModelB.Embed("taxi receipt"), ModelB.ModelId);
        Expect($"index {index2} built beside index 1 and switched: top hit {migrated[0].EvidenceId}; index 1 keeps {store.Count("SELECT COUNT(*) FROM chunks WHERE index_version = 1")} chunks for rollback",
            migrated[0].EvidenceId.StartsWith("contoso-taxi@v2", StringComparison.Ordinal)
            && store.Count("SELECT COUNT(*) FROM chunks WHERE index_version = 1") > 0);
        store.SwitchIndex(1);
        Expect($"rollback: index {store.CurrentIndex().Version} ({store.CurrentIndex().Model}) answers again",
            Search(store, "contoso", "taxi receipt").Count > 0);

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    /// <summary>The whole pipeline for one source: discover, validate, index, activate.</summary>
    static async Task<ChangeNotice> IngestAsync(IngestionStore store, PolicyDocument doc)
    {
        long id = await PrepareAsync(store, doc);
        return store.Activate(id, Today);
    }

    static async Task<long> PrepareAsync(IngestionStore store, PolicyDocument doc)
    {
        IngestResult found = store.Discover(doc);
        store.Validate(found.Id);
        await store.IndexAsync(found.Id, ModelA, ModelA.ModelId);
        return found.Id;
    }

    /// <summary>The first attempt: two autocommit statements, a crash can fall between them.</summary>
    static void NaiveActivate(string path, string tenant, string docId, long id, bool crashBetween)
    {
        using var db = new SqliteConnection($"Data Source={path};Pooling=False");
        db.Open();
        using SqliteCommand retire = db.CreateCommand();
        retire.CommandText = "UPDATE versions SET state = 'Superseded' WHERE tenant = $t AND doc_id = $d AND state = 'Active'";
        retire.Parameters.AddWithValue("$t", tenant);
        retire.Parameters.AddWithValue("$d", docId);
        retire.ExecuteNonQuery();
        if (crashBetween) throw new InvalidOperationException("simulated crash between the two updates");
        using SqliteCommand activate = db.CreateCommand();
        activate.CommandText = "UPDATE versions SET state = 'Active' WHERE id = $id";
        activate.Parameters.AddWithValue("$id", id);
        activate.ExecuteNonQuery();
    }

    static IReadOnlyList<IndexedChunk> Search(IngestionStore store, string tenant, string query, int top = 3)
    {
        (_, string model) = store.CurrentIndex();
        HashingEmbedder embedder = model == ModelA.ModelId ? ModelA : ModelB;
        return store.Search(tenant, embedder.Embed(query), model, top);
    }

    static VersionState State(IngestionStore store, string docId, int version) =>
        store.Versions(docId.StartsWith("fabrikam", StringComparison.Ordinal) ? "fabrikam" : "contoso", docId)
             .Single(v => v.Version == version).State;

    static long ActiveCount(IngestionStore store, string docId) =>
        store.Count("SELECT COUNT(*) FROM versions WHERE doc_id = $d AND state = 'Active'", ("$d", docId));

    static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}

/// <summary>Test double: deletes the document while its chunks are being embedded.</summary>
internal sealed class DeleteWhileEmbedding(HashingEmbedder inner, Action delete) : IEmbeddingGenerator<string, Embedding<float>>
{
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        GeneratedEmbeddings<Embedding<float>> result = await inner.GenerateAsync(values, options, cancellationToken);
        delete();
        return result;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}