using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;

namespace Ch02.TokenExplorer;

// Offline proof: dotnet run -- --self-test
// The expected counts were read once from Microsoft.ML.Tokenizers 2.0.0 and
// pinned here, so a package bump that changes a vocabulary fails the test.
public static class TokenSelfTest
{
    static int s_passed, s_total;

    public static int Run()
    {
        Tokenizer o200k = TiktokenTokenizer.CreateForEncoding("o200k_base");
        Tokenizer cl100k = TiktokenTokenizer.CreateForEncoding("cl100k_base");

        // Pinned counts: (sample, o200k, cl100k).
        (Sample Sample, int O200k, int Cl100k)[] pinned =
        [
            (Samples.English, 21, 21),
            (Samples.Arabic, 33, 70),
            (Samples.CSharp, 39, 39),
        ];
        foreach ((Sample sample, int o, int c) in pinned)
        {
            Expect($"{sample.Label}: o200k_base counts {o} tokens", o200k.CountTokens(sample.Text) == o);
            Expect($"{sample.Label}: cl100k_base counts {c} tokens", cl100k.CountTokens(sample.Text) == c);
        }

        // Failure (a): the chars-per-token estimate is wrong outside English prose.
        // For this C# sample o200k lands near len/4; the drift that matters is Arabic.
        Expect("DEFECT REPRODUCED: len/4 undercounts the Arabic sample by more than 25%",
            RelativeError(Samples.Arabic, o200k) < -0.25);
        Expect("len/4 stays within 25% for the English and C# samples",
            Math.Abs(RelativeError(Samples.English, o200k)) <= 0.25 && Math.Abs(RelativeError(Samples.CSharp, o200k)) <= 0.25);
        Expect("the two encodings disagree on the Arabic sample",
            o200k.CountTokens(Samples.Arabic.Text) != cl100k.CountTokens(Samples.Arabic.Text));

        // Context budget: the reserve is part of the plan.
        ContextBudget plan = new(8_192, 600, 2_400, 2_000, 800, 1_024);
        Expect("the planned request fits with 1,368 tokens free", plan.Fits && plan.Free == 1_368);
        Expect("history growth that eats the output reserve is refused",
            !(plan with { History = 4_200 }).Fits);
        Expect("an input that fits the window but not the reserve is refused",
            !(plan with { History = 3_800 }).Fits && (plan with { History = 3_800 }).Input < plan.Window);

        // Message list: every turn re-sends the whole list.
        List<ChatMessage> list = [new(ChatRole.System, "Policy assistant."), new(ChatRole.User, "Hi")];
        int before = TokenMath.CountMessages(list, o200k);
        list.Add(new(ChatRole.Assistant, "Hello."));
        list.Add(new(ChatRole.User, "Hi"));
        Expect("the second turn costs more input than the first", TokenMath.CountMessages(list, o200k) > before);

        // Sampling arithmetic.
        Dictionary<string, double> next = new() { ["a"] = 0.45, ["b"] = 0.25, ["c"] = 0.20, ["d"] = 0.10 };
        Expect("a low temperature concentrates mass on the top token",
            TokenMath.ApplyTemperature(next, 0.2)["a"] > 0.9);
        Expect("temperature 1 leaves the distribution unchanged",
            Math.Abs(TokenMath.ApplyTemperature(next, 1.0)["b"] - 0.25) < 1e-9);
        Expect("top-p 0.7 keeps the two most likely tokens", TokenMath.TopP(next, 0.7) is ["a", "b"]);

        float[] v = [0.9f, 0.1f, 0.3f, 0.0f];
        Expect("a vector is identical to itself (cosine 1)", Math.Abs(TokenMath.Cosine(v, v) - 1) < 1e-6);

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    static double RelativeError(Sample sample, Tokenizer tokenizer)
    {
        int real = tokenizer.CountTokens(sample.Text);
        return (double)(TokenMath.EstimateTokens(sample.Text) - real) / real;
    }

    static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}
