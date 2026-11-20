// ============================================================================
// Unbounded consumption: runaway loops and denial of wallet (LLM10:2025)
// ----------------------------------------------------------------------------
// Three caps on the agent loop, each proven with a deterministic test double
// (a small local model rarely loops on demand):
//
//   1. FunctionInvokingChatClient.MaximumIterationsPerRequest stops a model
//      that asks for the same tool forever (RunawayClient).
//   2. TokenBudgetClient reserves an estimate of each call BEFORE the call
//      (its worst case only when MaxOutputTokens is set) and refuses when the
//      reservation does not fit, so concurrent requests cannot share one
//      remainder. Spending can still pass the budget by the gap between the
//      estimate and the real usage. A check after the call cannot refuse at
//      all: the tokens are already spent.
//   3. A deadline (CancellationToken) bounds wall-clock time.
//
// Rate limiting at the API edge is the hosting chapter's; this is the loop.
//   dotnet run -- --self-test     prints RESULT n/n, exit code 1 on any failure
// ============================================================================

using System.ComponentModel;
using System.Diagnostics;
using Ch20.ConsumptionLimits;
using Microsoft.Extensions.AI;

bool selfTest = args.Contains("--self-test");
Console.WriteLine(selfTest
    ? "=== Unbounded consumption: self-test ==="
    : "=== Unbounded consumption (no model; the same suite runs with --self-test) ===");

int passed = 0, total = 0;
void Expect(string name, bool ok)
{
    total++;
    if (ok) passed++;
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
}

// ── 1. Iteration cap ───────────────────────────────────────────────────
Console.WriteLine("\n-- 1. MaximumIterationsPerRequest stops a runaway tool loop --");
const int IterationCap = 4;
int toolCalls = 0;

[Description("Returns the next migration step. Always reports more work to do.")]
string NextStep([Description("the current step number")] int step)
{
    Interlocked.Increment(ref toolCalls);
    return $"Step {step} done. Call next_step with step={step + 1}.";
}

RunawayClient runaway = new();
IChatClient capped = runaway.AsBuilder()
    .UseFunctionInvocation(configure: c => c.MaximumIterationsPerRequest = IterationCap)
    .Build();
ChatOptions loopOptions = new()
{
    Tools = [AIFunctionFactory.Create((Delegate)NextStep, name: "next_step")],
    AllowMultipleToolCalls = false,   // advisory: the connector decides whether it forwards this
};
ChatResponse capResult = await capped.GetResponseAsync(
    [new ChatMessage(ChatRole.User, "Run the migration to completion.")], loopOptions);
Expect($"cap {IterationCap}: {toolCalls} tool calls, {runaway.Calls} model calls, then the loop returned",
    toolCalls == IterationCap && runaway.Calls == IterationCap + 1);
Expect($"the last model reply is returned as it came: {capResult.Messages.Last().Contents.OfType<FunctionCallContent>().Count()} tool call(s) left unexecuted",
    capResult.Messages.Last().Contents.OfType<FunctionCallContent>().Any());

// ── 2. Token budget ────────────────────────────────────────────────────
Console.WriteLine("\n-- 2. Token budget: reserve before the call --");
{
    MeteredClient metered = new(tokensPerCall: 400);
    TokenBudgetClient budget = new(metered, budget: 1000, reservePerCall: 400);
    List<string> outcomes = [];
    for (int i = 1; i <= 4; i++)
    {
        try
        {
            await budget.GetResponseAsync([new ChatMessage(ChatRole.User, $"request {i}")]);
            outcomes.Add("ok");
        }
        catch (TokenBudgetExceededException)
        {
            outcomes.Add("refused");
        }
    }
    Expect($"budget 1000, 400 per call: [{string.Join(", ", outcomes)}], {metered.Calls} calls made, {budget.Spent} spent",
        outcomes.SequenceEqual(["ok", "ok", "refused", "refused"]) && metered.Calls == 2 && budget.Spent == 800);
}
{
    MeteredClient metered = new(tokensPerCall: 400);
    FirstEditionBudgetClient v1 = new(metered, budget: 1000);
    int ok = 0;
    try
    {
        for (int i = 1; i <= 4; i++) { await v1.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]); ok++; }
    }
    catch (InvalidOperationException) { }
    Expect($"DEFECT REPRODUCED: the first-edition check after the call spent {v1.TokensUsed} of 1000 in {metered.Calls} calls " +
        $"and threw away a paid response ({ok} returned)", v1.TokensUsed == 1200 && metered.Calls == 3 && ok == 2);
}
{
    MeteredClient metered = new(tokensPerCall: 400, delay: TimeSpan.FromMilliseconds(30));
    TokenBudgetClient budget = new(metered, budget: 2000, reservePerCall: 400);
    int admitted = 0, refused = 0;
    Task[] callers = [.. Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
    {
        try
        {
            await budget.GetResponseAsync([new ChatMessage(ChatRole.User, "go")]);
            Interlocked.Increment(ref admitted);
        }
        catch (TokenBudgetExceededException)
        {
            Interlocked.Increment(ref refused);
        }
    }))];
    await Task.WhenAll(callers);
    Expect($"20 concurrent callers (Task.Run), budget 2000: {admitted} admitted, {refused} refused, " +
        $"peak {metered.PeakConcurrency} in flight, {budget.Spent} spent",
        admitted == 5 && refused == 15 && metered.Calls == 5 && budget.Spent <= 2000 && metered.PeakConcurrency > 1);
}
{
    // The reservation is an estimate. A call that uses more than it reserved is
    // charged what it used, so the budget is exceeded by the difference.
    MeteredClient metered = new(tokensPerCall: 900);
    TokenBudgetClient budget = new(metered, budget: 500, reservePerCall: 400);
    await budget.GetResponseAsync([new ChatMessage(ChatRole.User, "one long answer")]);
    Expect($"LIMIT: reserved 400, the call used 900: {budget.Spent} spent of a 500 budget (set MaxOutputTokens to bound it)",
        budget.Spent == 900);
}

// ── 3. Deadline ────────────────────────────────────────────────────────
Console.WriteLine("\n-- 3. A per-request deadline --");
{
    SlowClient slow = new(TimeSpan.FromSeconds(30));
    using CancellationTokenSource deadline = new(TimeSpan.FromMilliseconds(200));
    long start = Stopwatch.GetTimestamp();
    bool cancelled = false;
    try
    {
        await slow.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: deadline.Token);
    }
    catch (OperationCanceledException)
    {
        cancelled = true;
    }
    TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
    Expect($"a 30 s call is cancelled at the 200 ms deadline (under 5 s)", cancelled && elapsed < TimeSpan.FromSeconds(5));
}

Console.WriteLine($"\nRESULT {passed}/{total}");
return passed == total ? 0 : 1;
