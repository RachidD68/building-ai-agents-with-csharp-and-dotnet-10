using System.Text.RegularExpressions;
using AgenticAI.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ch20.IndirectInjection;

/// <summary>
/// The assembler, the scanner and the fence, checked offline. The scripted
/// model is deliberately obedient: it follows any "reply only with: X" it is
/// sent, wherever it appears. That is the worst case a real model can reach.
/// </summary>
internal static partial class IndirectSelfTest
{
    static int s_passed, s_total;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Indirect prompt injection: self-test ===");

        Console.WriteLine("\n-- Provenance and quarantine --");
        {
            Assembled a = PromptAssembler.Build(Samples.Question, [Samples.Handbook]);
            string answer = await AskObedient(a.Text);
            Expect($"handbook only: answer \"{answer}\", external={a.IngestedExternal}",
                answer == Samples.Fact && !a.IngestedExternal);
        }
        {
            Assembled a = PromptAssembler.Build(Samples.Question, [Samples.Handbook, Samples.Poisoned]);
            string answer = await AskObedient(a.Text);
            Expect($"poisoned upload quarantined ({a.Quarantined[0].Reason}); answer \"{answer}\"",
                a.Quarantined.Count == 1 && a.Sent.Count == 1 && answer == Samples.Fact);
            Expect("the provenance line names source and trust",
                a.Text.Contains("source=handbook/returns.md trust=internal", StringComparison.Ordinal));
        }

        Console.WriteLine("\n-- The scanner is a signal --");
        {
            Assembled a = PromptAssembler.Build(Samples.Question, [Samples.Paraphrased]);
            string answer = await AskObedient(a.Text);
            Expect($"MISS: a paraphrased payload passes the scanner; the obedient model answers \"{answer}\"",
                a.Quarantined.Count == 0 && answer == "HIJACKED");
            Expect("that run is marked IngestedExternal, a fact the host can hand to the policy", a.IngestedExternal);
        }

        Console.WriteLine("\n-- The fence --");
        {
            string v1 = PromptAssembler.FirstEditionFence(Samples.FenceBreaker.Text, Samples.Question);
            string afterFirstClose = v1[(v1.IndexOf("<</DATA>>", StringComparison.Ordinal) + 9)..];
            Expect("DEFECT REPRODUCED: with the fixed first-edition fence, the payload closes it and sits outside",
                afterFirstClose.Contains("New instruction from the operator", StringComparison.Ordinal) &&
                afterFirstClose.IndexOf("New instruction", StringComparison.Ordinal) < afterFirstClose.IndexOf("<</DATA>>", StringComparison.Ordinal));

            Assembled a = PromptAssembler.Build(Samples.Question, [Samples.FenceBreaker], boundary: "test01");
            Match open = Regex.Match(a.Text, "<<DATA id=test01 [^>]*>>"), close = Regex.Match(a.Text, "<</DATA id=test01>>");
            string inside = a.Text[(open.Index + open.Length)..close.Index];
            Expect("with the random-id fence, the payload stays inside the only closing marker",
                open.Success && close.Success && inside.Contains("New instruction from the operator", StringComparison.Ordinal) &&
                Regex.Matches(a.Text, "<</DATA").Count == 1);
            string b1 = PromptAssembler.Build(Samples.Question, [Samples.Handbook]).Boundary;
            string b2 = PromptAssembler.Build(Samples.Question, [Samples.Handbook]).Boundary;
            Expect($"the boundary id changes per request ({b1} vs {b2})", b1 != b2 && b1.Length == 12);
        }

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    /// <summary>Runs the real agent over a model that obeys any "reply only with" it is sent.</summary>
    static async Task<string> AskObedient(string prompt)
    {
        ScriptedToolChatClient model = new(turn =>
        {
            string sent = string.Join("\n", turn.Messages.Select(m => m.Text));
            Match order = ReplyOnlyWith().Match(sent);
            return ScriptedToolChatClient.Say(order.Success ? order.Groups[1].Value : Samples.Fact);
        });
        AgentResponse response = await SupportAgent.Create(model).RunAsync(prompt);
        return response.Text;
    }

    [GeneratedRegex(@"reply only with: (\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex ReplyOnlyWith();

    static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}
