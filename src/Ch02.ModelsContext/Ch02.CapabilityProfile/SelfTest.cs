using System.Net.Http;
using Microsoft.Extensions.AI;
using static Ch02.CapabilityProfile.ScriptedChatClient;

namespace Ch02.CapabilityProfile;

// Offline proof: dotnet run -- --self-test
// A scripted client truncates, refuses, answers wrong and throws; the profiler
// must put each trial in its own bucket and say what the responses show.
public static class SelfTest
{
    static int s_passed, s_total;

    public static async Task<int> RunAsync()
    {
        CancellationToken none = CancellationToken.None;
        ChatOptions capped = new() { MaxOutputTokens = 8, ResponseFormat = ChatResponseFormat.Json };

        // One trial per task, in task order: taxi-42, taxi-64, limit-50, self-approval, currency.
        ScriptedChatClient script = new(
            _ => Reply("""{"decision":"allow"}""", ChatFinishReason.Stop, output: 6),
            _ => Reply("""{"decision":"allow"}""", ChatFinishReason.Stop, output: 6),
            _ => throw new HttpRequestException("Connection refused (localhost:11434)"),
            _ => Reply("""{"decision":"al""", ChatFinishReason.Length, output: 8),
            _ => Reply("I'm sorry, I can't decide expense claims.", ChatFinishReason.Stop, output: 7));
        ConfigurationResult run = await Profiler.RunAsync(
            new("scripted", "fake", "script-1", script, capped), ContosoTasks.All, trials: 1, none);

        Outcome[] expected = [Outcome.Correct, Outcome.Wrong, Outcome.TransportError, Outcome.Truncated, Outcome.Refused];
        Expect("each trial lands in its own bucket: correct, wrong, transport, truncated, refused",
            run.Trials.Select(t => t.Outcome).SequenceEqual(expected));
        Expect("the run goes on after a transport error (5 trials recorded)", run.Trials.Count == 5);
        Expect("the counts keep the buckets apart (1 each, 0 invalid, 0 provider error)",
            expected.All(o => run.Counts[o] == 1) && run.Counts[Outcome.Invalid] == 0 && run.Counts[Outcome.ProviderError] == 0);

        // Failure (b): an output cap cuts the JSON mid-object.
        ProfileTask taxi = ContosoTasks.All[0];
        ChatResponse cut = Reply("""{"decision":"al""", ChatFinishReason.Length);
        Expect("DEFECT REPRODUCED: a parse-only check calls the cut reply invalid and hides the cap",
            Profiler.TryReadDecision(cut.Text) is null);
        Expect("the finish reason is read first: Length means Truncated", Profiler.Classify(taxi, cut).Outcome == Outcome.Truncated);
        Expect("a reply cut at the cap stays Truncated even when the fragment parses",
            Profiler.Classify(taxi, Reply("""{"decision":"allow"}""", ChatFinishReason.Length)).Outcome == Outcome.Truncated);
        Expect("ContentFilter is a refusal, not an invalid reply",
            Profiler.Classify(taxi, Reply("", ChatFinishReason.ContentFilter)).Outcome == Outcome.Refused);
        Expect("prose instead of the JSON contract is Invalid",
            Profiler.Classify(taxi, Reply("Sure! The decision is allow.", ChatFinishReason.Stop)).Outcome == Outcome.Invalid);
        Expect("a decision outside allow/review/deny is Invalid",
            Profiler.Classify(taxi, Reply("""{"decision":"approve"}""", ChatFinishReason.Stop)).Outcome == Outcome.Invalid);

        // Errors: transport versus provider versus the caller's own cancellation.
        ConfigurationResult errors = await Profiler.RunAsync(new("errors", "fake", "script-2", new ScriptedChatClient(
                _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout."),
                _ => throw new InvalidOperationException("model does not support thinking")),
            new ChatOptions()), ContosoTasks.All[..2], trials: 1, none);
        Expect("an HttpClient timeout is a TransportError", errors.Trials[0].Outcome == Outcome.TransportError);
        Expect("a provider that rejects the request is a ProviderError, with its message kept",
            errors.Trials[1].Outcome == Outcome.ProviderError && errors.Trials[1].Detail.Contains("does not support thinking"));
        using CancellationTokenSource stopped = new();
        stopped.Cancel();
        bool rethrown = false;
        try { await Profiler.RunAsync(new("stopped", "fake", "script-3", script, capped), ContosoTasks.All, 1, stopped.Token); }
        catch (OperationCanceledException) { rethrown = true; }
        Expect("the caller's cancellation stops the run instead of counting as a failure", rethrown);

        Expect("every call gets its own copy of the options, with the cap in it",
            script.OptionsSeen.Count >= 5 && script.OptionsSeen.All(o => o is { MaxOutputTokens: 8 } && !ReferenceEquals(o, capped)));

        // Failure (c): an option that is silently ignored.
        ConfigurationResult ignored = await Profiler.RunAsync(new("ignores-cap", "fake", "script-4",
            new ScriptedChatClient(_ => Reply("""{"decision":"allow"}""", ChatFinishReason.Stop, output: 40)), capped),
            ContosoTasks.All[..1], 1, none);
        string capEvidence = ignored.Options.Single(o => o.Option == "MaxOutputTokens").Evidence;
        Expect("DEFECT REPRODUCED: the request still says MaxOutputTokens=8 while the reply used 40",
            capped.MaxOutputTokens == 8 && ignored.Trials[0].OutputTokens == 40);
        Expect("the profile reports the ignored cap from the usage", capEvidence.StartsWith("IGNORED"));
        Expect("a cap the replies respect is reported as honored",
            run.Options.Single(o => o.Option == "MaxOutputTokens").Evidence.StartsWith("honored"));

        ChatOptions reasoning = new() { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low } };
        string NoEvidence = (await Profiler.RunAsync(new("r1", "fake", "m", new ScriptedChatClient(
            _ => Reply("""{"decision":"allow"}""", ChatFinishReason.Stop, output: 6)), reasoning), ContosoTasks.All[..1], 1, none))
            .Options.Single(o => o.Option == "Reasoning").Evidence;
        string Observed = (await Profiler.RunAsync(new("r2", "fake", "m", new ScriptedChatClient(
            _ => Reply("""{"decision":"allow"}""", ChatFinishReason.Stop, output: 36, reasoning: 30)), reasoning), ContosoTasks.All[..1], 1, none))
            .Options.Single(o => o.Option == "Reasoning").Evidence;
        string Rejected = (await Profiler.RunAsync(new("r3", "fake", "m", new ScriptedChatClient(
            _ => throw new InvalidOperationException("does not support thinking")), reasoning), ContosoTasks.All[..1], 1, none))
            .Options.Single(o => o.Option == "Reasoning").Evidence;
        Expect("requested reasoning with no reasoning tokens or content: NO EVIDENCE", NoEvidence.StartsWith("NO EVIDENCE"));
        Expect("reasoning tokens in the usage: observed", Observed.StartsWith("observed"));
        Expect("every call rejected: the option was refused, not ignored", Rejected.StartsWith("rejected"));

        // The profile is data: dated, pinned, and it lists what did not run.
        CapabilityReport report = new(new DateTimeOffset(2026, 11, 20, 9, 30, 0, TimeSpan.Zero),
            CapabilityReport.CurrentPins(), 1, [run], ["cloud-json: not run (no key configured)"]);
        string md = report.ToMarkdown();
        Expect("the Markdown is dated and names the M.E.AI pin",
            md.Contains("# Capability profile, 2026-11-20 09:30 UTC") && md.Contains("Microsoft.Extensions.AI.Abstractions 10.11.0"));
        Expect("the table has separate Invalid, Refused, Truncated, Transport and Provider error columns",
            md.Contains("| Invalid | Refused | Truncated | Transport | Provider error |"));
        Expect("failed trials and configurations not run are listed",
            md.Contains("- scripted / limit-50 #1: TransportError") && md.Contains("- cloud-json: not run"));
        string folder = Path.Combine(Path.GetTempPath(), "ch02-profile-" + Guid.NewGuid().ToString("N"));
        try
        {
            (string json, string mdPath) = report.Write(folder);
            Expect("the files carry the run date in their names",
                Path.GetFileName(json) == "capability-profile-2026-11-20.json" && File.Exists(mdPath)
                && File.ReadAllText(json).Contains("\"Outcome\": \"Truncated\""));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}
