using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using CommunityToolkit.VectorData.InMemory;

namespace Ch10.RagPipeline;

/// <summary>
/// Offline checks of the pipeline with fixed vectors: dotnet run -- --self-test.
/// No embedding model: a generator returns hand-written vectors by text.
/// </summary>
internal static class SelfTest
{
    private static int s_passed, s_total;

    private static void Check(bool ok, string name)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== RAG pipeline: self-test (fixed vectors) ===");

        Console.WriteLine("\n-- 1. Chunking --");
        DocumentChunk[] chunks = [.. Pipeline.Chunk("values.txt",
            "Contoso Values\n\nIntegrity first: we do the right thing.\n\nOk.\n\nOne team: we win together.")];
        Console.WriteLine($"  chunks: {string.Join(", ", chunks.Select(c => c.Id))}");
        Check(chunks.Select(c => c.Id).SequenceEqual(["values.txt-1", "values.txt-3"]),
            "paragraphs under 20 characters are skipped; ids keep the paragraph index");

        Console.WriteLine("\n-- 2. The failure: a model with another vector size, no check --");
        FixedEmbeddings wide = new(dimensions: 1536);
        InMemoryCollection<string, DocumentChunk> unchecked1536 = new("unchecked");
        await unchecked1536.EnsureCollectionExistsAsync();
        string stored;
        try
        {
            DocumentChunk raw = chunks[0];
            raw.Embedding = (await wide.GenerateAsync(raw.Content)).Vector;   // CheckDimensions skipped
            await unchecked1536.UpsertAsync(raw);
            DocumentChunk? back = await unchecked1536.GetAsync(raw.Id, new() { IncludeVectors = true });
            stored = back is null ? "not stored" : $"stored with {back.Embedding.Length} dimensions";
        }
        catch (Exception ex) { stored = $"refused: {ex.GetType().Name}"; }
        Console.WriteLine($"  InMemoryCollection, schema 768, 1536-dimension vector: {stored}");
        Check(stored == "stored with 1536 dimensions",
            "DEFECT REPRODUCED: the in-memory store accepts a vector that does not match its schema");

        string search;
        try
        {
            ReadOnlyMemory<float> query = (await new FixedEmbeddings().GenerateAsync("values")).Vector;
            int n = 0;
            await foreach (VectorSearchResult<DocumentChunk> _ in unchecked1536.SearchAsync(query, 3)) n++;
            search = $"returned {n} hit(s)";
        }
        catch (Exception ex) { search = $"threw {ex.GetType().Name}"; }
        Console.WriteLine($"  a 768-dimension query against it: {search}");
        Check(search.StartsWith("threw", StringComparison.Ordinal),
            "the mismatch surfaces only at query time, after ingestion");

        // The same wrong model on both paths: every vector is 1536, so nothing ever compares sizes.
        string sameModel;
        try
        {
            ReadOnlyMemory<float> query = (await wide.GenerateAsync("values")).Vector;
            int n = 0;
            await foreach (VectorSearchResult<DocumentChunk> _ in unchecked1536.SearchAsync(query, 3)) n++;
            sameModel = $"returned {n} hit(s)";
        }
        catch (Exception ex) { sameModel = $"threw {ex.GetType().Name}"; }
        Console.WriteLine($"  a 1536-dimension query (the same wrong model): {sameModel}");
        Check(sameModel.StartsWith("returned", StringComparison.Ordinal),
            "DEFECT REPRODUCED: with the same wrong model on both paths, the store never complains");

        Console.WriteLine("\n-- 3. The fix: the dimension check at startup --");
        string startup;
        try { await Pipeline.CheckModelAsync(wide); startup = "passed"; }
        catch (InvalidOperationException ex) { startup = ex.Message; }
        Console.WriteLine($"  {startup.Split(". ")[0]}.");
        Check(startup.StartsWith("The embedding model returned 1536 dimensions, but DocumentChunk declares 768", StringComparison.Ordinal),
            "a 1536-dimension model fails at startup, before any file is read");

        InMemoryCollection<string, DocumentChunk> guarded = new("guarded");
        await guarded.EnsureCollectionExistsAsync();
        bool threw = false;
        try { await Pipeline.IngestAsync(wide, guarded, [.. Pipeline.Chunk("values.txt", "Integrity first: we do the right thing.")]); }
        catch (InvalidOperationException) { threw = true; }
        int count = 0;
        await foreach (DocumentChunk _ in guarded.GetAsync(_ => true, 10)) count++;
        Check(threw && count == 0, $"IngestAsync with the wrong model stores nothing ({count} records)");

        bool ok768;
        try { await Pipeline.CheckModelAsync(new FixedEmbeddings()); ok768 = true; } catch { ok768 = false; }
        Check(ok768, "a 768-dimension model passes the check");

        Console.WriteLine("\n-- 4. Retrieval with fixed vectors --");
        FixedEmbeddings fixedModel = new();
        InMemoryCollection<string, DocumentChunk> store = new("documents");
        await store.EnsureCollectionExistsAsync();
        DocumentChunk[] docs =
        [
            new() { Id = "benefits.txt-1", SourceFile = "benefits.txt", Content = "pto: 15 days of paid time off." },
            new() { Id = "benefits.txt-2", SourceFile = "benefits.txt", Content = "pto and parental leave: 16 weeks." },
            new() { Id = "values.txt-1", SourceFile = "values.txt", Content = "values: integrity first." },
        ];
        await Pipeline.IngestAsync(fixedModel, store, docs);
        List<VectorSearchResult<DocumentChunk>> hits = await Pipeline.SearchAsync(fixedModel, store, "pto", top: 3);
        foreach (VectorSearchResult<DocumentChunk> h in hits)
            Console.WriteLine($"  {h.Score:F4}  {h.Record.Id}");
        Check(hits.Select(h => h.Record.Id).SequenceEqual(["benefits.txt-1", "benefits.txt-2", "values.txt-1"]),
            "hits come back best first");
        Check(Math.Abs((hits[0].Score ?? 0) - 1.0) < 1e-6 && Math.Abs((hits[1].Score ?? 0) - Math.Sqrt(0.5)) < 1e-6
              && Math.Abs(hits[2].Score ?? 1) < 1e-6,
            "scores are cosine similarities: 1.0000, 0.7071, 0.0000");

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    /// <summary>
    /// Fixed vectors: each text starts with "topic:" or "topic and topic:"; axis 0
    /// is pto, axis 1 parental, axis 2 values. A bare word embeds as its own axis.
    /// </summary>
    private sealed class FixedEmbeddings(int dimensions = DocumentChunk.Dimensions)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        private static readonly string[] Axes = ["pto", "parental", "values"];

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<GeneratedEmbeddings<Embedding<float>>>([.. values.Select(text =>
            {
                float[] v = new float[dimensions];
                string head = text.Split(':')[0];
                for (int i = 0; i < Axes.Length; i++)
                    if (head.Contains(Axes[i], StringComparison.Ordinal)) v[i] = 1f;
                if (v.All(x => x == 0)) v[dimensions - 1] = 1f;
                return new Embedding<float>(v);
            })]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
