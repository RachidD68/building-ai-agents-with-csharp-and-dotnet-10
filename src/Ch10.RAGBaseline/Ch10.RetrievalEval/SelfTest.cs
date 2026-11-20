using System.Numerics.Tensors;

namespace Ch10.RetrievalEval;

/// <summary>
/// Offline checks of the metrics with fixed vectors: dotnet run -- --self-test.
/// Every chunk and question is a 2-D unit vector at a fixed angle, so each
/// cosine score is cos(angle difference) and every number below can be checked by hand.
/// </summary>
internal static class SelfTest
{
    // Chunk angles in degrees.
    private static readonly (string Id, double Angle)[] Chunks =
    [
        ("pto-0", 0), ("pto-1", 80), ("leave-a", 90), ("travel-0", 102),
        ("leave-b", 150), ("leave-c", 160), ("sick-0", 340),
    ];

    private static readonly (EvalQuestion Q, double Angle)[] Questions =
    [
        (new("q1", "How many PTO days do I get?", ["pto-0"]), 0),
        (new("q2", "Which kinds of leave are paid?", ["leave-a", "leave-b", "leave-c"]), 90),
        (new("q3", "What is the policy on cryptocurrency?", []), 210),
        (new("q4", "When do I need a doctor's note?", ["sick-0"]), 295),
    ];

    private static readonly double[] Sweep = [0.50, 0.55, 0.60, 0.65, 0.70, 0.75, 0.80];

    public static int Run()
    {
        Console.WriteLine("=== Retrieval evaluation: self-test (fixed 2-D vectors) ===");
        int passed = 0, total = 0;
        void Check(bool ok, string name)
        {
            total++;
            if (ok) passed++;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        }
        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-4;

        Dictionary<string, IReadOnlyList<Ranked>> rankings = Questions.ToDictionary(
            q => q.Q.Id, q => Rank(Vector(q.Angle)));
        EvalQuestion[] set = [.. Questions.Select(q => q.Q)];

        Console.WriteLine("\n-- 1. One relevant chunk against three (K = 3, no threshold) --");
        EvalReport open = RetrievalMetrics.Evaluate(set, rankings, k: 3, threshold: double.MinValue);
        foreach (QuestionScore s in open.Questions.Where(s => s.Question.Id is "q1" or "q2"))
        {
            Console.WriteLine($"  {s.Question.Id} relevant [{string.Join(", ", s.Question.Relevant)}]");
            Console.WriteLine($"     top 3 [{string.Join(", ", rankings[s.Question.Id].Take(3).Select(r => $"{r.ChunkId} {r.Score:F2}"))}]" +
                              $"  hit {(s.Hit ? 1 : 0)}  recall {s.Recall:F2}");
        }
        QuestionScore q1 = open.Questions[0], q2 = open.Questions[1];
        Check(q1.Hit && Near(q1.Recall, 1), "q1, one relevant chunk ranked first: HitRate@3 = Recall@3 = 1");
        Check(q2.Hit && Near(q2.Recall, 1.0 / 3), "q2, three relevant chunks, one in the top 3: hit = 1, Recall@3 = 0.33");

        Console.WriteLine("\n-- 2. The failure: \"recall@K\" counted as a hit (the first edition's definition) --");
        double v1Recall = open.Questions.Where(s => s.Question.Answerable).Average(s => s.Hit ? 1.0 : 0.0);
        Console.WriteLine($"  first-edition \"recall@3\" {v1Recall:F2}, Recall@3 {open.Recall:F2}");
        Check(Near(v1Recall, 1) && Near(open.Recall, 7.0 / 9),
            "DEFECT REPRODUCED: counting hits reports 1.00; the share of relevant chunks found is 0.78");
        Check(Near(open.HitRate, v1Recall), "that number is HitRate@3, so the report names it HitRate@3");

        Console.WriteLine("\n-- 3. Threshold sweep (K = 3) --");
        Console.WriteLine("  threshold  HitRate@3  Recall@3  no-answer abstained  answerable abstained");
        List<EvalReport> sweep = [.. Sweep.Select(t => RetrievalMetrics.Evaluate(set, rankings, 3, t))];
        foreach (EvalReport r in sweep)
            Console.WriteLine($"  {r.Threshold,9:F2}  {r.HitRate,9:F2}  {r.Recall,8:F2}  {r.CorrectAbstentions,10}/{r.NoAnswerCount}" +
                              $"  {r.FalseAbstentions,19}/{r.AnswerableCount}");
        EvalReport at055 = sweep.Single(r => Near(r.Threshold, 0.55));
        string q3At055 = string.Join(", ", at055.Questions.Single(s => s.Question.Id == "q3").Retrieved);
        Check(at055.CorrectAbstentions == 0 && q3At055 == "leave-c",
            $"at 0.55 the out-of-scope q3 still retrieves {q3At055} (score 0.64): no abstention");
        EvalReport at080 = sweep.Single(r => Near(r.Threshold, 0.80));
        Check(at080.FalseAbstentions == 1 && at080.CorrectAbstentions == 1,
            "at 0.80 q3 abstains, but so does the answerable q4 (best score 0.71)");
        EvalReport chosen = RetrievalMetrics.Choose(sweep);
        double[] tied = [.. sweep.Where(r => r.Correct == chosen.Correct).Select(r => r.Threshold)];
        Console.WriteLine($"  chosen {chosen.Threshold:F2}: {chosen.Correct}/{set.Length} correct; tied range {tied.Min():F2}-{tied.Max():F2}");
        Check(Near(chosen.Threshold, 0.65) && chosen.Correct == 4 && Near(tied.Max(), 0.70),
            "the sweep picks 0.65 (0.65-0.70 get all four right): a number from this set, not a rule");

        Console.WriteLine($"\nRESULT {passed}/{total}");
        return passed == total ? 0 : 1;
    }

    private static float[] Vector(double degrees) =>
        [(float)Math.Cos(degrees * Math.PI / 180), (float)Math.Sin(degrees * Math.PI / 180)];

    private static IReadOnlyList<Ranked> Rank(float[] query) =>
        [.. Chunks
            .Select(c => new Ranked(c.Id, TensorPrimitives.CosineSimilarity(query, Vector(c.Angle))))
            .OrderByDescending(r => r.Score).ThenBy(r => r.ChunkId, StringComparer.Ordinal)];
}
