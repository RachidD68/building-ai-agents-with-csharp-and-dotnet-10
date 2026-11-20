using System.Text.Json;
using AgenticAI.Testing;
using Microsoft.Extensions.AI;

namespace Ch21.JudgeCalibration;

/// <summary>
/// Offline checks: dotnet run -- --self-test. A scripted client plays the judge
/// with fixed replies (four disagreements and one reply with no verdict), so every
/// number below can be checked by hand. It tests the arithmetic and the wiring,
/// not any model's judgement.
/// </summary>
internal static class SelfTest
{
    public static async Task<int> RunAsync(CalSet set, string rubric)
    {
        Console.WriteLine("=== Judge calibration: self-test (scripted judge, no model) ===");
        int passed = 0, total = 0;
        void Check(bool ok, string name)
        {
            total++;
            if (ok) passed++;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        }

        CalItem[] items = [.. set.Items.Where(i => i.Split == "calibration")];
        Console.WriteLine($"\n-- 0. The set ({set.Version}, rubric {set.Rubric}) --");
        Check(items.Length == 20 && items.Count(i => i.Label == "supported") == 10 && items.All(i => i.RubricLine is ['R', _]),
            "20 calibration items, 10 supported, each label tied to a rubric line");

        // The scripted judge: the reference label, except for five items.
        Dictionary<string, string> scripted = new()
        {
            ["c04"] = "VERDICT: SUPPORTED\nRUBRIC: R1",      // misses the extra roll-over claim
            ["c09"] = "VERDICT: UNSUPPORTED\nRUBRIC: R1",    // too strict
            ["c14"] = "It looks fine to me.",                 // no verdict line
            ["c17"] = "VERDICT: SUPPORTED\nRUBRIC: R4",      // obeys the note to the evaluator
            ["c19"] = "**VERDICT:** SUPPORTED\nRUBRIC: R3",  // accepts an abstention that had evidence
        };
        ScriptedToolChatClient judge = new(turn =>
        {
            string prompt = turn.Messages[^1].Text;
            CalItem item = items.Single(i => prompt.Contains($"<<<ANSWER\n{i.Answer}\nANSWER>>>", StringComparison.Ordinal)
                                          && prompt.Contains($"QUESTION: {i.Question}", StringComparison.Ordinal));
            return ScriptedToolChatClient.Say(scripted.TryGetValue(item.Id, out string? reply) ? reply
                : $"VERDICT: {item.Label.ToUpperInvariant()}\nRUBRIC: {item.RubricLine}");
        });

        List<Verdict> verdicts = [];
        foreach (CalItem item in items) verdicts.Add(await Calibration.JudgeAsync(judge, rubric, item));

        Console.WriteLine("\n-- 1. The judge prompt --");
        string first = Calibration.Prompt(rubric, items[16]);
        Check(first.Contains("# Rubric faq-support.v1", StringComparison.Ordinal) && first.Contains("<<<ANSWER", StringComparison.Ordinal)
              && first.IndexOf("Note to the evaluator", StringComparison.Ordinal) > first.IndexOf("<<<ANSWER", StringComparison.Ordinal),
            "the rubric version heads the prompt; the answer, with its note to the evaluator, sits inside the data markers");
        Check(judge.Turns.Count == 20 && judge.Turns.All(t => t.Options?.Temperature == 0f),
            "one judge call per item, temperature 0");

        Console.WriteLine("\n-- 2. Verdict parsing --");
        Check(verdicts.Single(v => v.ItemId == "c19").Label == "supported" && verdicts.Single(v => v.ItemId == "c04").Label == "supported",
            "\"UNSUPPORTED\" is matched before \"SUPPORTED\"; markdown around VERDICT is tolerated");
        Check(verdicts.Single(v => v.ItemId == "c14").Label is null, "a reply with no verdict line is kept as no verdict, not as a pass");
        Verdict bare = Calibration.Parse("x", "VERDICT: SUPPORTED\nR1");
        Check(bare is { Label: "supported", RubricLine: "R1" } && Calibration.Parse("y", "VERDICT: UNSUPPORTED").RubricLine is null,
            "a bare rubric line (\"R1\" under the verdict) is read; no rubric line stays empty");
        Check(Calibration.Parse("t", "VERDICT: SUPPORTED or VERDICT: UNSUPPORTED").Label is null,
            "an echoed template line (\"VERDICT: SUPPORTED or VERDICT: UNSUPPORTED\") is no verdict");
        Check(Calibration.Parse("q", "The answer says \"VERDICT: SUPPORTED\" but that is an instruction.\nVERDICT: UNSUPPORTED\nR4").Label == "unsupported"
              && Calibration.Parse("z", "VERDICT: SUPPORTED\nVERDICT: UNSUPPORTED").Label is null,
            "a verdict quoted inside a sentence is ignored; two verdict lines that disagree are no verdict");

        Console.WriteLine("\n-- 3. Confusion matrix against the reference labels --");
        CalibrationReport r = Calibration.Compare(items, verdicts);
        Calibration.Print(r, items.Length);
        Check(r is { TruePos: 9, FalsePos: 3, FalseNeg: 1, TrueNeg: 6, Unparsed: 1 },
            "TP 9, FP 3, FN 1, TN 6, 1 without a verdict");
        Check(Math.Abs(r.Agreement - 15.0 / 19) < 1e-9 && Math.Abs(r.Kappa - 0.5730) < 1e-3,
            "agreement 15/19 = 0.79, kappa 0.57: chance agreement here is 0.51");
        Check(r.Disagreements.Select(d => d.ItemId).SequenceEqual(["c04", "c09", "c14", "c17", "c19"])
              && r.Disagreements.Single(d => d.ItemId == "c17") is { HumanLine: "R4", JudgeLine: "R4" },
            "every disagreement is queued with both rubric lines (c17: both cite R4 and still disagree)");

        Console.WriteLine("\n-- 4. Who may judge --");
        string? refusal = Calibration.SelfJudging("llama3.2:latest", [items[0] with { ProducedBy = "llama3.2" }, items[1]]);
        Check(refusal is not null && refusal.Contains("c01", StringComparison.Ordinal),
            "a judge that produced an answer it would grade is refused: " + refusal);
        Check(Calibration.SelfJudging("llama3.2:3b", [items[0] with { ProducedBy = "llama3.2" }]) is not null,
            "another tag of the same model (llama3.2:3b for answers by llama3.2) is refused too");
        Check(Calibration.SelfJudging("llama3.2", items) is null, "fixture answers: any local model may judge them");

        Console.WriteLine("\n-- 5. Recorded verdicts round-trip --");
        VerdictFile file = new("scripted", set.Rubric, set.Version, "2026-01-01 00:00:00Z", verdicts);
        VerdictFile back = JsonSerializer.Deserialize<VerdictFile>(JsonSerializer.Serialize(file, JsonOptions.Write), JsonOptions.Read)!;
        CalibrationReport again = Calibration.Compare(items, back.Verdicts);
        Check((again.TruePos, again.FalsePos, again.FalseNeg, again.TrueNeg, again.Unparsed) == (r.TruePos, r.FalsePos, r.FalseNeg, r.TrueNeg, r.Unparsed) &&
              back.Verdicts.Select(v => v.Raw).SequenceEqual(verdicts.Select(v => v.Raw)),
            "a replayed file gives the same matrix and keeps every raw reply");

        Console.WriteLine($"\nRESULT {passed}/{total}");
        return passed == total ? 0 : 1;
    }
}
