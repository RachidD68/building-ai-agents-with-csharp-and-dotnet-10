using AgenticAI.Testing;
using Ch16.HarnessLoop;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

/// <summary>Offline checks: dotnet run -- --self-test. A scripted worker writes numbered drafts.</summary>
static class SelfTest
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Harness loop self-test (offline) ===\n");
        int passed = 0, total = 0;
        void Check(string name, bool ok)
        {
            total++; passed += ok ? 1 : 0;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        }

        // 1-3. An evaluator that is never satisfied, cap 3.
        (ScriptedToolChatClient never, AgentResponse capped, RecordingLoopEvaluator[] neverJudge) =
            await LoopAsync(3, Judge(_ => LoopEvaluation.Continue("Shorter, and state the price.")));
        Check("DEFECT REPRODUCED: on the cap, RunAsync returns the last draft normally (3 calls, text \"draft 3\", finish Stop)",
            never.Turns.Count == 3 && capped.Text == "draft 3" && capped.FinishReason == ChatFinishReason.Stop);
        Check("the iteration that hits the cap is not evaluated (2 evaluations for 3 iterations)",
            neverJudge[0].Evaluations == 2);
        LoopOutcome cap = RecordingLoopEvaluator.Outcome(neverJudge);
        Check("the recording evaluator classifies it as CapReached, exit code 2",
            cap == LoopOutcome.CapReached && RecordingLoopEvaluator.ExitCode(cap) == 2);

        // 4. Stop on the second pass.
        (ScriptedToolChatClient early, _, RecordingLoopEvaluator[] earlyJudge) =
            await LoopAsync(3, Judge(ctx => ctx.Iteration >= 2 ? LoopEvaluation.Stop() : LoopEvaluation.Continue("again")));
        LoopOutcome stopped = RecordingLoopEvaluator.Outcome(earlyJudge);
        Check("an evaluator that says Stop on iteration 2: 2 calls, Stopped, exit code 0",
            early.Turns.Count == 2 && stopped == LoopOutcome.Stopped && RecordingLoopEvaluator.ExitCode(stopped) == 0);

        // 5. The feedback reaches the next pass.
        Check("Continue(feedback) text is in the request of the next iteration",
            never.Turns[1].Messages.Any(m => m.Text.Contains("state the price")));

        // 6. Two evaluators: one says Stop at once, the other wants three passes.
        (ScriptedToolChatClient two, _, RecordingLoopEvaluator[] pair) = await LoopAsync(5,
            Judge(_ => LoopEvaluation.Stop()),
            Judge(ctx => ctx.Iteration >= 3 ? LoopEvaluation.Stop() : LoopEvaluation.Continue("more")));
        Console.WriteLine($"  two evaluators (Stop at once / Stop at 3): {two.Turns.Count} iterations");
        Check("with two evaluators the loop goes on while either asks for another pass (3 iterations), then Stopped",
            two.Turns.Count == 3 && RecordingLoopEvaluator.Outcome(pair) == LoopOutcome.Stopped);

        // 7. No cap set.
        (ScriptedToolChatClient unbounded, _, _) = await LoopAsync(null, Judge(_ => LoopEvaluation.Continue("again")));
        Check($"MaxIterations left null: the loop runs LoopAgent.DefaultMaxIterations ({LoopAgent.DefaultMaxIterations}) times",
            unbounded.Turns.Count == LoopAgent.DefaultMaxIterations);

        // 8. The demo's rule, without a model.
        string longNoPrice = string.Join(' ', Enumerable.Repeat("word", 30));
        Check("SummaryRule: 30 words and no price -> 2 problems; 12 words with $79.99 -> none",
            SummaryRule.Problems(longNoPrice).Count == 2 &&
            SummaryRule.Problems("The Contoso Ergonomic Keyboard costs $79.99 and has a two-year warranty. DONE").Count == 0);

        Console.WriteLine($"\nRESULT {passed}/{total}");
        return passed == total ? 0 : 1;
    }

    private static DelegateLoopEvaluator Judge(Func<LoopContext, LoopEvaluation> rule) =>
        new((ctx, _) => ValueTask.FromResult(rule(ctx)));

    private static async Task<(ScriptedToolChatClient, AgentResponse, RecordingLoopEvaluator[])> LoopAsync(
        int? max, params LoopEvaluator[] evaluators)
    {
        ScriptedToolChatClient client = new(turn => ScriptedToolChatClient.Say($"draft {turn.Call}"));
        RecordingLoopEvaluator[] recorders = [.. evaluators.Select(e => new RecordingLoopEvaluator(e))];
        LoopAgent loop = new(client.AsAIAgent(instructions: "Write a summary."), recorders,
            new LoopAgentOptions { MaxIterations = max, NonStreamingReturnsLastResponseOnly = true });
        AgentResponse response = await loop.RunAsync("Summarize the keyboard.", await loop.CreateSessionAsync());
        return (client, response, recorders);
    }
}
