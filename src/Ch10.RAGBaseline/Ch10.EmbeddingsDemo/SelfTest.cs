using System.Numerics.Tensors;

namespace Ch10.EmbeddingsDemo;

/// <summary>
/// Offline checks of cosine similarity with fixed vectors: dotnet run -- --self-test.
/// No embedding model is called.
/// </summary>
internal static class SelfTest
{
    public static int Run()
    {
        Console.WriteLine("=== Embeddings demo: self-test (fixed vectors) ===\n");
        int passed = 0, total = 0;
        void Check(bool ok, string name)
        {
            total++;
            if (ok) passed++;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        }

        float Cos(float[] a, float[] b) => TensorPrimitives.CosineSimilarity(a, b);
        bool Near(float value, double expected) => Math.Abs(value - expected) < 1e-6;

        float[] cat = [0.9f, 0.1f, 0f], feline = [0.8f, 0.2f, 0f], csharp = [0f, 0.1f, 0.9f];
        Console.WriteLine($"  cos(cat, feline) = {Cos(cat, feline):F4}, cos(cat, csharp) = {Cos(cat, csharp):F4}");
        Check(Cos(cat, feline) > Cos(cat, csharp), "the closer direction scores higher");
        Check(Near(Cos(cat, cat), 1), "a vector against itself: 1.0000");
        Check(Near(Cos([1f, 0f], [0f, 1f]), 0), "orthogonal vectors: 0.0000");
        Check(Near(Cos([1f, 2f], [-1f, -2f]), -1), "opposite vectors: -1.0000");
        Check(Near(Cos([1f, 2f], [10f, 20f]), 1), "length does not count, only direction: 1.0000");

        string mismatch;
        try { Cos(new float[768], new float[1536]); mismatch = "no error"; }
        catch (ArgumentException ex) { mismatch = ex.GetType().Name; }
        Console.WriteLine($"  768 against 1536 dimensions: {mismatch}");
        Check(mismatch == nameof(ArgumentException), "vectors of different sizes cannot be compared");

        Console.WriteLine($"\nRESULT {passed}/{total}");
        return passed == total ? 0 : 1;
    }
}
