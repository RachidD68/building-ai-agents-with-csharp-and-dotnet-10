// ============================================================================
// Scoped cache: a cached answer obeys the same boundary as a fresh one
// ----------------------------------------------------------------------------
// Two caches, retrieved chunks and generated answers, keyed by an HMAC of the
// authorization scope (tenant, access level), the normalized question, the
// corpus or index version, the model and the prompt version. Each entry knows
// the document versions it was built from. Revoking a document removes every
// entry built from it immediately; the TTL only bounds age. Delivery re-checks
// that each document is still readable by the scope; a write computed before a
// revocation in its tenant is refused.
//
// No model and no network: the answers are built from the evidence ids by a
// stand-in for generation, over the real authorized search of the chapter.
//   dotnet run                    runs the scenario and prints it
//   dotnet run -- --self-test     the same, as a test: RESULT n/n, exit code 1 on failure
// ============================================================================

using Ch11.AuthorizedSearch;
using Ch11.ScopedCache;
using CommunityToolkit.VectorData.InMemory;

bool selfTest = args.Contains("--self-test");
Console.WriteLine(selfTest
    ? "=== Scoped cache: self-test ==="
    : "=== Scoped cache (no model; the same suite runs with --self-test) ===");
return await Suite.RunAsync();

internal static class Suite
{
    const string PromptVersion = "faq-prompt-3";
    const string Model = "scripted";
    static readonly CallerContext Alice = new("contoso", "E-1001", AccessLevel.Staff);
    static readonly CallerContext Bob = new("contoso", "E-2002", AccessLevel.Staff);
    static readonly CallerContext Maria = new("contoso", "M-3003", AccessLevel.Manager);
    static readonly CallerContext Frank = new("fabrikam", "F-1", AccessLevel.Staff);
    static int s_passed, s_total;

    public static async Task<int> RunAsync()
    {
        IReadOnlyList<PolicyDocument> corpus = Corpus.Load();
        string corpusVersion = Corpus.Version();
        var search = new AuthorizedSearchService(new HashingEmbedder(), new InMemoryCollection<string, PolicyChunk>("policies"));
        await search.IndexAsync(corpus);
        var source = new SourcePolicy(corpus, search);
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 13, 17, 0, 0, TimeSpan.Zero));   // Friday
        var cache = new ScopedCache(clock, TimeSpan.FromMinutes(10), "sample-only-secret"u8.ToArray());
        var pipeline = new Pipeline(cache, search, source, corpusVersion);
        const string Taxi = "Can I expense a taxi ride on a business trip?";

        // ---- First attempts ------------------------------------------------------
        Console.WriteLine("\n-- Where it fails: first attempts --");
        var naive = new Dictionary<string, string>();                 // keyed by the question only
        string aliceAnswer = Pipeline.Answer(await search.SearchAsync(Alice, Taxi));
        naive[Taxi] = aliceAnswer;
        Expect($"DEFECT REPRODUCED: question-only key; Frank (fabrikam) is served \"{naive[Taxi]}\"",
            naive.TryGetValue(Taxi, out string? served) && served.Contains("contoso-", StringComparison.Ordinal));

        var ttlOnly = new ScopedCache(clock, TimeSpan.FromMinutes(10), "sample-only-secret"u8.ToArray());
        var ttlPipeline = new Pipeline(ttlOnly, search, SourcePolicy.TrustEverything, corpusVersion);
        await ttlPipeline.AskAsync(Alice, "How much is a hotel night?");
        source.SetAccess("contoso", "contoso-hotel", AccessLevel.Manager);    // hotel rates move to managers only
        clock.Advance(TimeSpan.FromMinutes(2));
        AskResult stale = await ttlPipeline.AskAsync(Alice, "How much is a hotel night?");
        Expect($"DEFECT REPRODUCED: TTL only, no revocation, no delivery check; 2 minutes later Alice still gets \"{stale.Answer}\"",
            stale.FromCache && stale.Answer.Contains("contoso-hotel", StringComparison.Ordinal));
        source.SetAccess("contoso", "contoso-hotel", AccessLevel.Staff);

        // ---- Scoped keys ------------------------------------------------------------
        Console.WriteLine("\n-- Scoped, versioned keys --");
        AskResult a1 = await pipeline.AskAsync(Alice, Taxi);
        AskResult f1 = await pipeline.AskAsync(Frank, Taxi);
        Expect($"Frank asks Alice's question: {(f1.FromCache ? "hit" : "miss")}, \"{f1.Answer}\"",
            !a1.FromCache && !f1.FromCache && f1.Answer.Contains("fabrikam-taxi", StringComparison.Ordinal)
            && !f1.Answer.Contains("contoso-", StringComparison.Ordinal));
        AskResult b1 = await pipeline.AskAsync(Bob, "  can i EXPENSE a taxi ride on a business trip? ");
        Expect($"Bob, same tenant and level, same question in other spacing and case: {(b1.FromCache ? "hit" : "miss")}", b1.FromCache);
        AskResult m1 = await pipeline.AskAsync(Maria, Taxi);
        Expect($"Maria (manager) asks it: {(m1.FromCache ? "hit" : "miss")}: another scope, another entry", !m1.FromCache);

        string key = cache.AnswerKey(CacheScope.Of(Alice), Taxi, corpusVersion, Model, PromptVersion);
        Expect($"the key carries no question text and no subject: {key[..23]}...",
            !key.Contains("taxi", StringComparison.OrdinalIgnoreCase) && !key.Contains("E-1001", StringComparison.Ordinal) && key.Length == 7 + 64);
        AskResult nextCorpus = await new Pipeline(cache, search, source, "2026-11-16.2").AskAsync(Alice, Taxi);
        Expect($"a new corpus version is a new key: {(nextCorpus.FromCache ? "hit" : "miss")}", !nextCorpus.FromCache);

        // ---- Revocation -------------------------------------------------------------
        Console.WriteLine("\n-- Revocation under a TTL --");
        int fabrikamBefore = cache.CountFor("fabrikam");
        clock.Advance(TimeSpan.FromMinutes(1));
        await source.RevokeAsync("contoso", "contoso-taxi");
        int removed = cache.Revoke("contoso", "contoso-taxi");
        AskResult afterRevoke = await pipeline.AskAsync(Alice, Taxi);
        Expect($"revoke contoso-taxi with 9 minutes of TTL left: {removed} entries removed (answers and chunks); Alice: " +
               $"{(afterRevoke.FromCache ? "hit" : "miss")}, \"{afterRevoke.Answer}\"",
            removed >= 2 && !afterRevoke.FromCache && !afterRevoke.Answer.Contains("contoso-taxi", StringComparison.Ordinal));
        Expect($"Fabrikam's entries are untouched ({cache.CountFor("fabrikam")} of {fabrikamBefore})",
            cache.CountFor("fabrikam") == fabrikamBefore && (await pipeline.AskAsync(Frank, Taxi)).FromCache);

        await pipeline.AskAsync(Alice, "How much is a hotel night?");
        source.SetAccess("contoso", "contoso-hotel", AccessLevel.Manager);     // no revocation notice sent
        AskResult checkedAtDelivery = await pipeline.AskAsync(Alice, "How much is a hotel night?");
        Expect($"access raised to manager, no notice: the delivery check refuses the entry ({checkedAtDelivery.Lookup}); fresh answer \"{checkedAtDelivery.Answer}\"",
            checkedAtDelivery.Lookup == "dependency_not_readable" && !checkedAtDelivery.Answer.Contains("contoso-hotel", StringComparison.Ordinal));
        source.SetAccess("contoso", "contoso-hotel", AccessLevel.Staff);

        clock.Advance(TimeSpan.FromMinutes(11));
        AskResult expired = await pipeline.AskAsync(Frank, Taxi);
        Expect($"Frank's entry after the TTL: {expired.Lookup}", expired.Lookup == "expired");

        // ---- A revocation that lands while an answer is being computed --------------
        Console.WriteLine("\n-- Revocation during a computation (two threads) --");
        const string Hotel = "How much is a hotel night?";
        CacheScope aliceScope = CacheScope.Of(Alice);
        string hotelKey = cache.AnswerKey(aliceScope, Hotel, corpusVersion, Model, PromptVersion);

        async Task<bool> RaceAsync(bool tokenBeforeRetrieval)
        {
            var retrieved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var revokedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<bool> compute = Task.Run(async () =>
            {
                long early = cache.BeginCompute("contoso");
                IReadOnlyList<EvidenceRecord> evidence = await search.SearchAsync(Alice, Hotel);
                retrieved.SetResult();
                await revokedTcs.Task;                                   // the revocation lands here
                long token = tokenBeforeRetrieval ? early : cache.BeginCompute("contoso");
                return cache.TryPut(hotelKey, Pipeline.Answer(evidence), aliceScope, evidence.Select(Dependency.Of), token);
            });
            Task revoke = Task.Run(async () =>
            {
                await retrieved.Task;
                await source.RevokeAsync("contoso", "contoso-hotel");
                cache.Revoke("contoso", "contoso-hotel");
                revokedTcs.SetResult();
            });
            await Task.WhenAll(compute, revoke);
            return compute.Result;
        }

        bool naiveStored = await RaceAsync(tokenBeforeRetrieval: false);
        Expect($"DEFECT REPRODUCED: token read at write time; the answer built from the revoked hotel policy is stored ({naiveStored})",
            naiveStored && cache.TryGet(hotelKey, (_, _) => true).Hit);
        string deliveryReason = cache.TryGet(hotelKey, source.Readable).Reason;     // a refusal also removes the entry
        Expect($"... and the delivery check still refuses it: {deliveryReason}", deliveryReason == "dependency_not_readable");
        source.Restore("contoso", "contoso-hotel");
        await source.RestoreIndexAsync("contoso", "contoso-hotel");
        bool fixedStored = await RaceAsync(tokenBeforeRetrieval: true);
        Expect($"token read before retrieval: the write is refused ({!fixedStored}), nothing stored",
            !fixedStored && !cache.TryGet(hotelKey, (_, _) => true).Hit);

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }

    internal sealed record AskResult(string Answer, bool FromCache, string Lookup);

    /// <summary>Cache first, then authorized retrieval, then a stand-in for generation.</summary>
    internal sealed class Pipeline(ScopedCache cache, AuthorizedSearchService search, SourcePolicy source, string corpusVersion)
    {
        public async Task<AskResult> AskAsync(CallerContext caller, string question)
        {
            CacheScope scope = CacheScope.Of(caller);
            string answerKey = cache.AnswerKey(scope, question, corpusVersion, Model, PromptVersion);
            CacheLookup lookup = cache.TryGet(answerKey, source.Readable);
            if (lookup.Hit) return new AskResult(lookup.Value!, true, lookup.Reason);

            long token = cache.BeginCompute(caller.Tenant);                  // before retrieval
            IReadOnlyList<EvidenceRecord> evidence = await search.SearchAsync(caller, question);
            Dependency[] deps = [.. evidence.Select(Dependency.Of)];
            cache.TryPut(cache.ChunkKey(scope, question, "index-1"),
                string.Join(",", evidence.Select(e => e.EvidenceId)), scope, deps, token);
            string answer = Answer(evidence);
            if (evidence.Count > 0) cache.TryPut(answerKey, answer, scope, deps, token);
            return new AskResult(answer, false, lookup.Reason);
        }

        public static string Answer(IReadOnlyList<EvidenceRecord> evidence) =>
            evidence.Count == 0 ? "no_authorized_evidence" : $"answer from {string.Join(", ", evidence.Select(e => e.EvidenceId))}";
    }
}

/// <summary>The source of truth for access: who may read which document now.</summary>
internal sealed class SourcePolicy
{
    private readonly Dictionary<(string, string), (AccessLevel Access, bool Revoked)> _docs = [];
    private readonly Dictionary<(string, string), (AccessLevel Access, bool Revoked)> _original = [];
    private readonly AuthorizedSearchService? _search;

    public static SourcePolicy TrustEverything { get; } = new([], null) { _trustAll = true };
    private bool _trustAll;

    public SourcePolicy(IEnumerable<PolicyDocument> corpus, AuthorizedSearchService? search)
    {
        _search = search;
        foreach (PolicyDocument d in corpus.Where(d => d.State == SourceState.Active))
            _docs[(d.Tenant, d.DocId)] = _original[(d.Tenant, d.DocId)] = (d.Access, false);
    }

    public bool Readable(CacheScope scope, Dependency d) =>
        _trustAll || (scope.Tenant == d.Tenant && _docs.TryGetValue((d.Tenant, d.DocId), out var doc)
                      && !doc.Revoked && doc.Access <= scope.Level);

    public void SetAccess(string tenant, string docId, AccessLevel access)
    {
        if (_trustAll) return;
        _docs[(tenant, docId)] = _docs[(tenant, docId)] with { Access = access };
        UpdateIndexAsync(tenant, docId, c => c.Access = (int)access).GetAwaiter().GetResult();
    }

    public async Task RevokeAsync(string tenant, string docId)
    {
        _docs[(tenant, docId)] = _docs[(tenant, docId)] with { Revoked = true };
        await UpdateIndexAsync(tenant, docId, c => c.State = nameof(SourceState.Revoked));
    }

    public void Restore(string tenant, string docId) => _docs[(tenant, docId)] = _original[(tenant, docId)];

    public Task RestoreIndexAsync(string tenant, string docId) =>
        UpdateIndexAsync(tenant, docId, c => c.State = nameof(SourceState.Active));

    private async Task UpdateIndexAsync(string tenant, string docId, Action<PolicyChunk> change)
    {
        if (_search is null) return;
        List<PolicyChunk> chunks = [];
        await foreach (PolicyChunk c in _search.Store.GetAsync(c => c.Tenant == tenant && c.DocId == docId, 100))
            chunks.Add(c);
        foreach (PolicyChunk c in chunks.Where(c => c.State is "Active" or "Revoked"))
        {
            change(c);
            await _search.Store.UpsertAsync(c);
        }
    }
}

internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}
