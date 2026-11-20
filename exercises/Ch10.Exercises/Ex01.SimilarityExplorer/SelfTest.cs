namespace Ex01.SimilarityExplorer;

/// <summary>Offline checks with fixed vectors: dotnet run -- --self-test. No model is called.</summary>
internal static class SelfTest
{
    public static int Run()
    {
        Console.WriteLine("=== Similarity explorer: self-test (fixed vectors) ===\n");
        int passed = 0, total = 0;
        void Check(bool ok, string name)
        {
            total++;
            if (ok) passed++;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        }

        string[] texts = ["The cat sat on the mat.", "A kitten was resting on the rug.",
                          "The stock market crashed yesterday.", "I love programming in C#."];
        ReadOnlyMemory<float>[] vectors = [new[] { 1f, 0f, 0f }, new[] { 0.8f, 0.6f, 0f },
                                           new[] { 0f, 0f, 1f }, new[] { 0f, 1f, 0f }];
        SimilarityIndex index = new(texts, vectors);

        IReadOnlyList<(string Text, float Score)> top = index.Top(new[] { 1f, 0f, 0f });
        foreach ((string text, float score) in top) Console.WriteLine($"  {score:F4}  \"{text}\"");
        Check(top.Count == 3 && top[0].Text == texts[0] && top[1].Text == texts[1],
            "top 3, best first: the cat, then the kitten");
        Check(Math.Abs(top[0].Score - 1) < 1e-6 && Math.Abs(top[1].Score - 0.8) < 1e-6 && Math.Abs(top[2].Score) < 1e-6,
            "scores are cosine similarities: 1.0000, 0.8000, 0.0000");

        string refused;
        try { index.Top(new float[768]); refused = "searched"; }
        catch (InvalidOperationException ex) { refused = ex.Message; }
        Console.WriteLine($"  {refused}");
        Check(refused.StartsWith("The query has 768 dimensions but the corpus has 3", StringComparison.Ordinal),
            "a query from another model is reported before searching");

        bool corpusRefused = false;
        try { _ = new SimilarityIndex(texts[..2], [new[] { 1f, 0f }, new[] { 1f, 0f, 0f }]); }
        catch (InvalidOperationException) { corpusRefused = true; }
        Check(corpusRefused, "a corpus whose vectors differ in size is refused when the index is built");

        Console.WriteLine($"\nRESULT {passed}/{total}");
        return passed == total ? 0 : 1;
    }
}
