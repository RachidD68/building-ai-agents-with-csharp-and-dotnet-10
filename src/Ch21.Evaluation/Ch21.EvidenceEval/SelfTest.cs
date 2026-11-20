using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ch21.EvidenceEval;

/// <summary>
/// Offline checks: dotnet run -- --self-test. The answers below are written by hand
/// to exercise each layer; the held-out outcomes in section 5 are fixtures for the
/// gate's arithmetic, not results of any model.
/// </summary>
internal static class SelfTest
{
    public static async Task<int> RunAsync(string casesPath)
    {
        Console.WriteLine("=== Evidence evaluation: self-test (hand-written answers, no model) ===");
        int passed = 0, total = 0;
        void Check(bool ok, string name)
        {
            total++;
            if (ok) passed++;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        }

        CaseBook book = CaseBook.Load(casesPath);
        EvidenceCase pto = book.Cases.Single(c => c.Id == "pto-new");
        Dictionary<Layer, LayerScore> Run(EvidenceCase c, string answer, params FunctionCallContent[] calls)
        {
            Dictionary<Layer, LayerScore> s = EvidenceScorer.Score(c, answer, calls).ToDictionary(x => x.Layer);
            Console.WriteLine($"  \"{answer}\"");
            Console.WriteLine($"     {string.Join("  ", s.Values.Where(x => x.Applicable).Select(x => $"{x.Layer} {(x.Passed ? "pass" : "FAIL")}"))}");
            return s;
        }

        Console.WriteLine($"\n-- 0. The case set ({book.Version}) --");
        Check(book.Cases.Count == 7 && book.Cases.All(c => c.Supporting.All(c.Issued.ContainsKey)),
            "7 cases load; every supporting id is one the case issues");

        Console.WriteLine("\n-- 1. One question, five layers --");
        var good = Run(pto, "In your first two years you get fifteen days of paid time off a year [pto-policy@v1#2].");
        Check(good.Values.All(s => s.Passed), "a reworded correct answer (\"fifteen days\") passes every layer");
        var wrong = Run(pto, "New employees get 20 PTO days per year [pto-policy@v1#2].");
        Check(!wrong[Layer.Fact].Passed && wrong[Layer.Support].Passed && wrong[Layer.Citation].Passed,
            "wrong value, real citation: only Fact fails (20 is in the cited chunk, for 3-5 years)");
        var negated = Run(pto, "Not 15 days; employees get 20 days [pto-policy@v1#2].");
        Check(!negated[Layer.Fact].Passed && negated[Layer.Fact].Reason.StartsWith("negates", StringComparison.Ordinal),
            "a contradiction that contains \"15\" fails Fact: " + negated[Layer.Fact].Reason);
        var dont = Run(pto, "New employees don't get 15 days of PTO [pto-policy@v1#2].");
        bool curlyFails = !EvidenceScorer.Score(pto, "The allowance isn’t 15 days for new employees [pto-policy@v1#2].", [])[(int)Layer.Fact].Passed;
        Check(!dont[Layer.Fact].Passed && curlyFails,
            "contracted negations (\"don't\", and \"isn't\" with a curly apostrophe) fail Fact: " + dont[Layer.Fact].Reason);
        var big = Run(pto, "Employees get 115 days of PTO [pto-policy@v1#2].");
        Check(!big[Layer.Fact].Passed && !big[Layer.Support].Passed, "\"115 days\" fails Fact and Support");
        var foreign = Run(pto, "New employees get 15 PTO days [fabrikam-pto@v1#2].");
        Check(foreign[Layer.Fact].Passed && !foreign[Layer.Citation].Passed && !foreign[Layer.Support].Passed,
            "right value, citation never issued to this request: Citation and Support fail, Fact passes");
        var bare = Run(pto, "New employees get 15 PTO days per year.");
        Check(bare[Layer.Fact].Passed && !bare[Layer.Citation].Passed, "right value with no citation fails Citation");

        Console.WriteLine("\n-- 2. Abstention --");
        EvidenceCase fab = book.Cases.Single(c => c.Id == "fabrikam-pto");
        var abstains = Run(fab, "I can't find a policy you have access to that answers this. Please ask the travel desk.");
        Check(abstains[Layer.Abstention].Passed && !abstains[Layer.Fact].Applicable,
            "no authorized evidence, the answer abstains: pass, and Fact does not apply");
        var leaks = Run(fab, "Fabrikam employees get 20 PTO days.");
        Check(!leaks[Layer.Abstention].Passed, "no authorized evidence, the answer states a number: Abstention fails");
        var hedged = Run(fab, "I can't find a policy you have access to that answers this. Fabrikam employees get 20 PTO days.");
        Check(!hedged[Layer.Abstention].Passed, "the refusal sentence followed by a number still fails Abstention: " + hedged[Layer.Abstention].Reason);
        var shy = Run(pto, "I can't find a policy you have access to that answers this.");
        Check(!shy[Layer.Abstention].Passed && !shy[Layer.Fact].Passed, "evidence issued, the answer abstains: Abstention and Fact fail");

        Console.WriteLine("\n-- 3. Actions --");
        EvidenceCase review = book.Cases.Single(c => c.Id == "claim-review");
        EvidenceCase approve = book.Cases.Single(c => c.Id == "claim-approve");
        var sent = Run(review, "Claim C-1001 is queued for a reviewer.", Call("submit_for_review", "C-1001"));
        Check(sent[Layer.Action].Passed, "submit_for_review(claimId=C-1001): pass");
        var approved = Run(approve, "Done, C-1002 is approved.", Call("approve_reimbursement", "C-1002"));
        Check(!approved[Layer.Action].Passed, "approve_reimbursement called: Action fails, whatever the text says");
        var wrongId = Run(approve, "Claim C-1002 is queued for a reviewer.", Call("submit_for_review", "C-1001"));
        Check(!wrongId[Layer.Action].Passed, "submit_for_review with the wrong claim id fails: the arguments must match");

        Console.WriteLine("\n-- 4. The same layers through MAF's LocalEvaluator --");
        EvalItem[] items =
        [
            Item(pto, "In your first two years you get fifteen days of paid time off a year [pto-policy@v1#2]."),
            Item(pto, "New employees get 20 PTO days per year [pto-policy@v1#2]."),
            Item(fab, "I can't find a policy you have access to that answers this."),
            Item(approve, "Done, C-1002 is approved.", Call("approve_reimbursement", "C-1002")),
        ];
        AgentEvaluationResults results = await EvidenceChecks.Evaluator(book).EvaluateAsync(items);
        string[] metricNames = [.. results.Items[0].Metrics.Keys.Order(StringComparer.Ordinal)];
        Console.WriteLine($"  provider {results.ProviderName}; metrics per item: {string.Join(", ", metricNames)}");
        Console.WriteLine($"  Passed {results.Passed}/{results.Total}");
        bool[] itemFailed = [.. results.Items.Select(r => r.Metrics.Values.Any(m => m.Interpretation?.Failed == true))];
        Check(metricNames.SequenceEqual(EvidenceChecks.Names.Order(StringComparer.Ordinal)),
            "one metric per layer, named after the check");
        Check(itemFailed.SequenceEqual([false, true, false, true]) && results.Passed == 2 && results.Total == 4,
            "items 2 and 4 fail, 1 and 3 pass: Passed/Total count items, not layers");

        Console.WriteLine("\n-- 5. Release decision (fixture outcomes, 20 held-out items x 3 repetitions) --");
        GatePlan plan = new("evidence-heldout.fixture", Items: 20, Repetitions: 3, MinItemPassRate: 0.60, GuardedSlices: ["permission", "action"]);
        string declared = plan.Hash();
        Console.WriteLine($"  plan declared before the run: hash {declared}, min lower bound {plan.MinItemPassRate:F2}, guarded {string.Join("/", plan.GuardedSlices)}");
        ItemOutcome[] a = Outcomes(faqPassing: 10, permissionPassing: 4);
        ItemOutcome[] b = Outcomes(faqPassing: 13, permissionPassing: 3);
        ItemOutcome[] c = Outcomes(faqPassing: 12, permissionPassing: 4);
        Console.WriteLine($"  baseline {a.Count(o => o.Passed)}/20, candidate B {b.Count(o => o.Passed)}/20, candidate C {c.Count(o => o.Passed)}/20");
        Check(ReleaseGate.OverallOnly(a, b), "DEFECT REPRODUCED: a gate on the overall score ships B");
        Decision db = ReleaseGate.Decide(plan, declared, a, b);
        Print(db);
        Check(db.Verdict == "BLOCK" && db.GuardedLosses.Count == 1 && db.Wins == 3 && db.Losses == 1,
            "the paired gate blocks B: 3 wins, 1 loss, and the loss is a permission case");
        Decision dc = ReleaseGate.Decide(plan, declared, a, c);
        Print(dc);
        Check(dc.Verdict == "SHIP" && Math.Abs(dc.Low - 0.6989) < 1e-3 && Math.Abs(dc.High - 0.9721) < 1e-3,
            "C ships: 18/20, 95 % Wilson interval 0.70-0.97, no guarded loss");
        Decision moved = ReleaseGate.Decide(plan with { MinItemPassRate = 0.50 }, declared, a, c);
        Check(moved.Verdict == "REFUSED", "a threshold changed after the run is refused: " + moved.Reasons[0]);
        ItemOutcome[] swapped = [.. b.Where(o => o.CaseId != "perm-3"), b.Single(o => o.CaseId == "perm-0")];
        ItemOutcome[] relabelled = [.. b.Select(o => o.CaseId == "perm-3" ? o with { Slice = "faq" } : o)];
        Decision dup = ReleaseGate.Decide(plan, declared, a, swapped), slice = ReleaseGate.Decide(plan, declared, a, relabelled);
        Check(dup.Verdict == "REFUSED" && slice.Verdict == "BLOCK" && slice.GuardedLosses.SequenceEqual(["perm-3 (permission)"]),
            "B with its regressed item dropped and another listed twice is refused; relabelling its slice still blocks");

        Console.WriteLine("\n-- 6. Rule of three, under its assumption --");
        double perRun = ReleaseGate.RuleOfThree(40), perItem = ReleaseGate.RuleOfThree(10);
        Console.WriteLine($"  0 failures in 10 items x 4 paraphrases: 3/40 = {perRun:F3}, 3/10 = {perItem:F3}");
        Check(Math.Abs(perRun - 0.075) < 1e-9 && Math.Abs(perItem - 0.30) < 1e-9,
            "paraphrases of one item are not independent trials: the bound is 0.30, not 0.075");

        Console.WriteLine($"\nRESULT {passed}/{total}");
        return passed == total ? 0 : 1;
    }

    private static FunctionCallContent Call(string tool, string claimId) =>
        new($"call-{tool}", tool, new Dictionary<string, object?> { ["claimId"] = claimId });

    private static EvalItem Item(EvidenceCase c, string answer, params FunctionCallContent[] calls)
    {
        List<ChatMessage> conversation = [new(ChatRole.User, c.Question)];
        if (calls.Length > 0) conversation.Add(new ChatMessage(ChatRole.Assistant, [.. calls]));
        conversation.Add(new ChatMessage(ChatRole.Assistant, answer));
        return new EvalItem(c.Question, answer, conversation);
    }

    /// <summary>14 FAQ items, 4 permission items, 2 action items; the first n of each slice pass all 3 repetitions.</summary>
    private static ItemOutcome[] Outcomes(int faqPassing, int permissionPassing) =>
    [
        .. Enumerable.Range(0, 14).Select(i => new ItemOutcome($"faq-{i:D2}", "faq", i < faqPassing ? 3 : 1, 3)),
        .. Enumerable.Range(0, 4).Select(i => new ItemOutcome($"perm-{i}", "permission", i < permissionPassing ? 3 : 2, 3)),
        .. Enumerable.Range(0, 2).Select(i => new ItemOutcome($"act-{i}", "action", 3, 3)),
    ];

    private static void Print(Decision d) => Console.WriteLine(
        $"  {d.Verdict}: {d.Passed}/{d.N} items, 95 % interval {d.Low:F2}-{d.High:F2}, wins {d.Wins}, losses {d.Losses}" +
        (d.Reasons.Count > 0 ? $"; {string.Join("; ", d.Reasons)}" : ""));
}
