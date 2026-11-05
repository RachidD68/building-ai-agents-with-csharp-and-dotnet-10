// ============================================================================
// Chapter 18 — Eval as a test suite: a deterministic regression harness
// ----------------------------------------------------------------------------
// This is the "treat your eval suite like your test suite" discipline made
// concrete. It is a plain console assertion harness (no xUnit, matching the
// book's other Tests projects) that runs a GOLDEN DATASET through a
// LocalEvaluator and asserts the whole batch passes.
//
// The trick that makes it a *test* rather than a *demo*: we evaluate
// PRE-BUILT responses, not a live agent. EvaluateAsync has an overload that
// takes the responses you already have (from a previous batch, a production
// log replay, or — here — a fixture) alongside the queries. No model is
// called, so the run is fast, free, and 100% deterministic: the same inputs
// always produce the same pass/fail. That is exactly what a regression test
// needs. The same shape moves to xUnit unchanged — turn each block into a
// [Fact] and let AssertAllPassed throw.
//
// Cases:
//   1. Golden responses that SHOULD pass  -> AssertAllPassed() succeeds.
//   2. Known-bad responses -> the evaluator catches them; we assert they fail.
//      The confabulated PTO answer from the chapter's opening anecdote, plus
//      two answers that merely CONTAIN "15" ("115 days", "Not 15 days ... 20").
//      This is the red half of red-green for agents: prove the check bites.
//
// What this check is — and is not: it is a VALUE check (does the answer state
// the policy number, and no other number of days?). It is a lexical signal,
// not proof that the answer is faithful to the source; a reworded correct
// answer can fail it and a wrong answer can be built to pass it. Faithfulness
// needs a judge or human labels — the chapter's groundedness section.
//
// Exit code 0 when every assertion holds; 1 otherwise.
// ============================================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

int failures = 0;

Console.WriteLine("=== Chapter 18: Eval test harness (deterministic, offline) ===");
Console.WriteLine();

// A stub agent: the pre-response EvaluateAsync overload needs an AIAgent for
// its signature, but it scores the responses we hand it and never calls the
// model. The stub's chat client throws if anything ever invokes it — proof the
// suite is deterministic.
ChatClientAgent agent = new(new UninvokedChatClient(), name: "FaqAgentUnderTest");

// The value check: every "<n> days" / "<n> PTO days" in the answer must be 15,
// and there must be at least one. A bare Contains("15") would accept
// "115 days" and "Not 15 days; employees get 20".
LocalEvaluator valueCheck = new(
    FunctionEvaluator.Create("pto_value_check",
        (string response) => PtoValue.StatesOnly(response, days: 15)));

string[] ptoQuery = ["How many PTO days do new employees get?"];

// ── Case 1: the golden answer passes ───────────────────────────────
await Case("correct PTO answer passes", expectPass: true,
    response: "New employees (0-2 years of service) receive 15 PTO days per year.");

// ── Case 2: the confabulated answer is caught (the opening incident) ──
await Case("confabulated PTO answer is caught", expectPass: false,
    response: "New employees get 20 PTO days per year.");

// ── Cases 3-4: answers that only CONTAIN "15" are caught too ────────
await Case("'115 days' is caught", expectPass: false,
    response: "Employees get 115 days of PTO.");
await Case("'Not 15 days ... 20' is caught", expectPass: false,
    response: "Not 15 days; employees get 20 days.");

Console.WriteLine();
if (failures == 0)
{
    Console.WriteLine("All eval-harness assertions passed.");
    return 0;
}

Console.WriteLine($"{failures} assertion(s) FAILED.");
return 1;

// ──────────────────────────────────────────────────────────────────
// One case: evaluate a single pre-built response and assert whether the
// value check passed or failed, the way a [Fact] would.
async Task Case(string name, bool expectPass, string response)
{
    AgentResponse[] responses = [new AgentResponse(new ChatMessage(ChatRole.Assistant, response))];

    AgentEvaluationResults results = await agent.EvaluateAsync(
        responses,
        ptoQuery,
        valueCheck);

    bool actuallyPassed = results.AllPassed;

    if (actuallyPassed == expectPass)
    {
        Console.WriteLine($"[pass] {name}  (Passed {results.Passed}/{results.Total})");
    }
    else
    {
        failures++;
        Console.WriteLine($"[FAIL] {name}  (expected AllPassed={expectPass}, got {actuallyPassed})");
    }
}

// A chat client that must never be called. If the eval ever tries to run the
// agent for real, this throws — which would mean the suite is no longer
// deterministic. It stays silent precisely because we evaluate stored responses.
internal sealed class UninvokedChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "The eval harness must not call the model — it scores stored responses.");

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "The eval harness must not call the model — it scores stored responses.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>Whole-number check of the day counts an answer states.</summary>
internal static class PtoValue
{
    static readonly System.Text.RegularExpressions.Regex DayCount =
        new(@"(?<!\d)(\d+)\s*(?:PTO\s+)?days?\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>True when the answer states at least one day count and every one equals <paramref name="days"/>.</summary>
    public static bool StatesOnly(string response, int days)
    {
        var counts = DayCount.Matches(response).Select(m => int.Parse(m.Groups[1].Value)).ToList();
        return counts.Count > 0 && counts.All(n => n == days);
    }
}
