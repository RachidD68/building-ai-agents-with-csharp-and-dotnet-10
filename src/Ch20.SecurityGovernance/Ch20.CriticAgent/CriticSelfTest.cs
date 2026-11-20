using AgenticAI.Testing;
using Microsoft.Extensions.AI;

namespace Ch20.CriticAgent;

/// <summary>
/// The real writer and critic agents over scripted models: the verdict is
/// parsed by RunAsync&lt;ValidationResult&gt;, then the gate decides.
/// </summary>
internal static class CriticSelfTest
{
    static int s_passed, s_total;

    const string CleanText = "A value type holds its data directly; a reference type holds a reference to an object on the heap.";

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Critic agent: self-test ===");

        Console.WriteLine("\n-- Agreement --");
        await Case("clean text, critic score 9", CleanText, Verdict(true, 9, pii: false), GateDecision.Publish, "critic_score");
        await Case("clean text, critic score 7 (threshold)", CleanText, Verdict(true, 7, pii: false), GateDecision.Publish, "critic_score");
        await Case("clean text, critic score 6", CleanText, Verdict(false, 6, pii: false), GateDecision.Reject, "critic_score");

        Console.WriteLine("\n-- Disagreement --");
        await Case("DISAGREEMENT: critic passes an email the detector finds",
            "Contact jane.doe@contoso.example for the migration guide.", Verdict(true, 9, pii: false),
            GateDecision.Reject, "pii_detector");
        await Case("DISAGREEMENT: critic flags a customer name the detector cannot see",
            "Our customer Jane Doe reported the bug first.", Verdict(false, 8, pii: true),
            GateDecision.Escalate, "critic_sees_pii_detector_does_not");
        await Case("critic says passed with a score of 4", CleanText, Verdict(true, 4, pii: false),
            GateDecision.Escalate, "critic_inconsistent");

        Console.WriteLine("\n-- No usable verdict fails closed --");
        await Case("critic answers in prose, not JSON", CleanText, "Looks good to me!", GateDecision.Reject, "critic_unavailable");
        await Case("critic score out of range (42)", CleanText, Verdict(true, 42, pii: false), GateDecision.Reject, "critic_out_of_range");

        Console.WriteLine("\n-- What a critic cannot judge --");
        await Case("SIGNAL MISS: 'I submitted 45 EUR for employee E-2002' (another tenant) is well written",
            "I have submitted the reimbursement of 45 EUR for employee E-2002. It will be paid on Friday.",
            Verdict(true, 9, pii: false), GateDecision.Publish, "critic_score");

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    static string Verdict(bool passed, int score, bool pii) =>
        $$"""{"passed":{{(passed ? "true" : "false")}},"score":{{score}},"factualAssessment":"ok","toneAssessment":"ok","piiDetected":{{(pii ? "true" : "false")}},"issues":[],"suggestions":[]}""";

    static async Task Case(string name, string writerText, string criticReply, GateDecision expected, string reason)
    {
        FakeChatClient writer = new(FakeReply.Text(writerText));
        FakeChatClient critic = new(FakeReply.Text(criticReply));
        CriticPipeline.Review review = await Agents.Pipeline(writer, critic).ReviewAsync("Explain value and reference types.");
        bool sawSchema = critic.Calls.Count == 1 && critic.Calls[0].Options?.ResponseFormat is ChatResponseFormatJson;
        Expect($"{name} -> {review.Gate.Decision} {review.Gate.Reason}",
            review.Gate.Decision == expected && review.Gate.Reason == reason && sawSchema);
    }

    static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}
