// ============================================================================
// Hybrid retrieval: lexical and vector candidates, an explicit fusion rule,
// and HitRate@K / Recall@K measured per stage
// ----------------------------------------------------------------------------
// Lexical search (BM25) finds exact names and identifiers; vector search finds
// a paraphrase. Reciprocal rank fusion combines the two lists by rank, because
// their scores share no scale. A deterministic reranker stands in for a hosted
// one. Each stage is measured on a small labeled set, two of whose questions
// need two pieces of evidence, which is where HitRate and Recall part ways.
//
// The chunk text comes from the chapter's corpus (contoso, staff, active); the
// vectors are fixed in hybrid-set.json, so no embedding model is involved.
//   dotnet run                    runs the measurement and prints it
//   dotnet run -- --self-test     the same, as a test: RESULT n/n, exit code 1 on failure
// ============================================================================

using System.Globalization;
using System.Text.Json;
using Ch11.AuthorizedSearch;
using Ch11.HybridRetrieval;

bool selfTest = args.Contains("--self-test");
Console.WriteLine(selfTest
    ? "=== Hybrid retrieval: self-test ==="
    : "=== Hybrid retrieval (no model; the same suite runs with --self-test) ===");
return Suite.Run();

internal static class Suite
{
    static int s_passed, s_total;

    public static int Run()
    {
        using JsonDocument set = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "hybrid-set.json")));
        int k = set.RootElement.GetProperty("k").GetInt32();
        int candidates = set.RootElement.GetProperty("candidates").GetInt32();
        Dictionary<string, string> text = Corpus.Load()
            .Where(d => d is { Tenant: "contoso", Access: AccessLevel.Staff, State: SourceState.Active })
            .SelectMany(d => d.Paragraphs().Select((p, i) => (Id: d.EvidenceId(i), Text: p)))
            .ToDictionary(x => x.Id, x => x.Text);
        List<Chunk> chunks = [.. set.RootElement.GetProperty("vectors").EnumerateObject()
            .Select(p => new Chunk(p.Name, text[p.Name], Floats(p.Value)))];
        Dictionary<string, Chunk> byId = chunks.ToDictionary(c => c.Id);
        var queries = set.RootElement.GetProperty("queries").EnumerateArray().Select(q => (
            Id: q.GetProperty("id").GetString()!, Text: q.GetProperty("text").GetString()!, Vector: Floats(q.GetProperty("vector")),
            Relevant: (IReadOnlyCollection<string>)[.. q.GetProperty("relevant").EnumerateArray().Select(r => r.GetString()!)])).ToList();

        var lexical = new LexicalIndex(chunks);
        var vector = new VectorIndex(chunks);
        IReranker reranker = new TermOverlapReranker();

        // ---- Every stage, every question ---------------------------------------
        var runs = new Dictionary<string, List<(IReadOnlyList<string> Retrieved, IReadOnlyCollection<string> Relevant)>>
        {
            ["lexical"] = [], ["vector"] = [], ["hybrid candidates"] = [], ["hybrid"] = [], ["hybrid + rerank"] = [],
        };
        var perQuery = new Dictionary<string, Dictionary<string, IReadOnlyList<string>>>();
        Console.WriteLine($"\n-- Top {k} per question (candidates: {candidates} from each list) --");
        foreach (var q in queries)
        {
            IReadOnlyList<Ranked> lex = lexical.Search(q.Text, candidates);
            IReadOnlyList<Ranked> vec = vector.Search(q.Vector, candidates);
            IReadOnlyList<Ranked> fused = Fusion.ReciprocalRank([lex, vec]);
            IReadOnlyList<Ranked> reranked = reranker.Rerank(q.Text, fused, byId);
            var stages = new Dictionary<string, IReadOnlyList<string>>
            {
                ["lexical"] = Ids(lex), ["vector"] = Ids(vec), ["hybrid candidates"] = Ids(fused),
                ["hybrid"] = Ids(fused), ["hybrid + rerank"] = Ids(reranked),
            };
            perQuery[q.Id] = stages;
            foreach ((string stage, IReadOnlyList<string> ids) in stages) runs[stage].Add((ids, q.Relevant));
            Console.WriteLine($"  {q.Id} ({q.Relevant.Count} relevant) \"{q.Text}\"");
            Console.WriteLine($"      lexical {Show(stages["lexical"], k)} | vector {Show(stages["vector"], k)} | hybrid {Show(stages["hybrid"], k)} | reranked {Show(stages["hybrid + rerank"], k)}");
        }

        Console.WriteLine($"\n-- Measures on this set (K = {k}; candidate set: all fused candidates) --");
        Console.WriteLine("  stage               HitRate@K  Recall@K");
        foreach ((string stage, var r) in runs)
        {
            int at = stage == "hybrid candidates" ? int.MaxValue : k;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {stage,-18}  {Metrics.HitRate(r, at),9:0.00}  {Metrics.Recall(r, at),8:0.00}"));
        }

        // ---- Rules ------------------------------------------------------------------
        Console.WriteLine("\n-- What the measures show --");
        bool In(string q, string stage, string id) => perQuery[q][stage].Take(k).Contains(id);
        Expect("an identifier (EXP-7): lexical finds it, vector does not",
            In("q1", "lexical", "contoso-hotel@v1#0") && !In("q1", "vector", "contoso-hotel@v1#0"));
        Expect($"a paraphrase (cab home late): vector finds it, lexical has {perQuery["q2"]["lexical"].Count} candidates",
            In("q2", "vector", "contoso-taxi@v2#0") && perQuery["q2"]["lexical"].Count == 0);
        Expect("hybrid finds both", In("q1", "hybrid", "contoso-hotel@v1#0") && In("q2", "hybrid", "contoso-taxi@v2#0"));

        Expect("Recall@K <= HitRate@K for every stage",
            runs.Values.All(r => Metrics.Recall(r, k) <= Metrics.HitRate(r, k) + 1e-9));
        var single = queries.Where(q => q.Relevant.Count == 1).Select(q => q.Id).ToHashSet();
        var multi = queries.Where(q => q.Relevant.Count > 1).Select(q => q.Id).ToHashSet();
        Expect($"one-evidence questions ({single.Count}): HitRate@K == Recall@K for every stage",
            runs.Keys.All(stage => Same(Pick(runs[stage], queries, single), k)));
        string[] differ = [.. runs.Keys.Where(stage => !Same(Pick(runs[stage], queries, multi), k))];
        Expect($"two-evidence questions ({multi.Count}): HitRate@K != Recall@K for {string.Join(", ", differ)}", differ.Length > 0);

        double hybridRecall = Metrics.Recall(runs["hybrid"], k);
        Expect(string.Create(CultureInfo.InvariantCulture,
                $"on this set, hybrid Recall@K {hybridRecall:0.00} >= lexical {Metrics.Recall(runs["lexical"], k):0.00} and vector {Metrics.Recall(runs["vector"], k):0.00}"),
            hybridRecall >= Metrics.Recall(runs["lexical"], k) && hybridRecall >= Metrics.Recall(runs["vector"], k));

        int lostAtCut = queries.Sum(q => q.Relevant.Count(id => perQuery[q.Id]["hybrid candidates"].Contains(id)
                                                               && !perQuery[q.Id]["hybrid + rerank"].Take(k).Contains(id)));
        Expect($"per stage: {lostAtCut} relevant chunks reach the candidates but not the final top {k}; the reranker adds no id",
            queries.All(q => perQuery[q.Id]["hybrid + rerank"].All(perQuery[q.Id]["hybrid candidates"].Contains)));

        // ---- Score fusion, first attempt --------------------------------------------
        Console.WriteLine("\n-- Where it fails: adding raw scores --");
        int flipped = 0, rrfChanged = 0;
        foreach (var q in queries)
        {
            IReadOnlyList<Ranked> lex = lexical.Search(q.Text, candidates), vec = vector.Search(q.Vector, candidates);
            IReadOnlyList<Ranked> scaledLex = [.. lex.Select(r => r with { Score = r.Score / 100 })];   // same ranking, another engine's scale
            if (SumScores(lex, vec)[0].Id != SumScores(scaledLex, vec)[0].Id) flipped++;
            if (!Ids(Fusion.ReciprocalRank([lex, vec])).SequenceEqual(Ids(Fusion.ReciprocalRank([scaledLex, vec])))) rrfChanged++;
        }
        Expect($"DEFECT REPRODUCED: score sum; BM25 scores divided by 100 (same order) changes the top result of {flipped} of {queries.Count} questions",
            flipped > 0);
        Expect($"reciprocal rank fusion under the same rescaling: {rrfChanged} changed", rrfChanged == 0);

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    static IReadOnlyList<Ranked> SumScores(IReadOnlyList<Ranked> a, IReadOnlyList<Ranked> b) =>
        [.. a.Concat(b).GroupBy(r => r.Id).Select(g => new Ranked(g.Key, g.Sum(r => r.Score)))
             .OrderByDescending(r => r.Score).ThenBy(r => r.Id, StringComparer.Ordinal)];

    static List<(IReadOnlyList<string>, IReadOnlyCollection<string>)> Pick(
        List<(IReadOnlyList<string> Retrieved, IReadOnlyCollection<string> Relevant)> runs,
        List<(string Id, string Text, float[] Vector, IReadOnlyCollection<string> Relevant)> queries, HashSet<string> ids) =>
        [.. runs.Where((_, i) => ids.Contains(queries[i].Id))];

    static bool Same(List<(IReadOnlyList<string> Retrieved, IReadOnlyCollection<string> Relevant)> runs, int k) =>
        Math.Abs(Metrics.HitRate(runs, k) - Metrics.Recall(runs, k)) < 1e-9;

    static IReadOnlyList<string> Ids(IReadOnlyList<Ranked> ranked) => [.. ranked.Select(r => r.Id)];

    static string Show(IReadOnlyList<string> ids, int k) =>
        ids.Count == 0 ? "-" : string.Join(" ", ids.Take(k).Select(id => id.Replace("contoso-", "", StringComparison.Ordinal)));

    static float[] Floats(JsonElement e) => [.. e.EnumerateArray().Select(x => x.GetSingle())];

    static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}
