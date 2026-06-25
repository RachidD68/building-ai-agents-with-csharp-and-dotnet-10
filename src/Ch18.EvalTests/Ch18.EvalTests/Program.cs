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
// Two cases:
//   1. Golden responses that SHOULD pass  -> AssertAllPassed() succeeds.
//   2. A known-bad response (the confabulated PTO answer from the chapter's
//      opening anecdote) -> the evaluator catches it; we assert that it fails.
//      This is the red half of red-green for agents: prove the check bites.
//
// Exit code 0 when every assertion holds; 1 otherwise.
// ============================================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

int failures = 0;

Console.WriteLine("=== Chapter 17: Eval test harness (deterministic, offline) ===");
Console.WriteLine();

// A stub agent: the pre-response EvaluateAsync overload needs an AIAgent for
// its signature, but it scores the responses we hand it and never calls the
// model. The stub's chat client throws if anything ever invokes it — proof the
// suite is deterministic.
ChatClientAgent agent = new(new UninvokedChatClient(), name: "FaqAgentUnderTest");

// The grounding check: a PTO answer is correct only if it contains "15".
LocalEvaluator groundingCheck = new(
    FunctionEvaluator.Create("pto_grounded",
        (string response) => response.Contains("15")));

string[] ptoQuery = ["How many PTO days do new employees get?"];

// ── Case 1: the golden (grounded) answer passes ───────────────────
await Case("grounded PTO answer passes", expectPass: true,
    response: "New employees (0-2 years of service) receive 15 PTO days per year.");

// ── Case 2: the confabulated answer is caught (the opening incident) ──
await Case("confabulated PTO answer is caught", expectPass: false,
    response: "New employees get 20 PTO days per year.");

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
// grounding check passed or failed, the way a [Fact] would.
async Task Case(string name, bool expectPass, string response)
{
    AgentResponse[] responses = [new AgentResponse(new ChatMessage(ChatRole.Assistant, response))];

    AgentEvaluationResults results = await agent.EvaluateAsync(
        responses,
        ptoQuery,
        groundingCheck);

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
