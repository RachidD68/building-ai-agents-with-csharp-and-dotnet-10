// ============================================================================
// Eval as a test suite: a deterministic regression harness
// ----------------------------------------------------------------------------
// A plain console assertion harness (no xUnit, matching the book's other Tests
// projects) that runs a GOLDEN DATASET through a LocalEvaluator and asserts
// which answers pass. It evaluates PRE-BUILT responses, not a live agent:
// EvaluateAsync has an overload that takes the responses you already have
// (a fixture here, a production log replay elsewhere). No model is called, so
// the same inputs always give the same verdicts. The same shape moves to xUnit
// unchanged: turn each case into a [Fact] and let AssertAllPassed throw.
//
// The red half comes first: a substring check (string.Contains, or MAF's
// EvalChecks.ContainsExpected) accepts "115 days" and "Not 15 days; ... 20".
// Then the value check that replaces it, named for what it measures
// (pto_value_check), rejects both and still passes the correct answer.
// It is a lexical value check, not a groundedness score.
//
//   dotnet run                    the harness (same as --self-test)
//   dotnet run -- --self-test     RESULT n/n; exit code 0 only when every check holds
// ============================================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

if (args.Length > 0 && !args.Contains("--self-test"))
{
    Console.WriteLine("usage: dotnet run [-- --self-test]");
    return 2;
}

int passed = 0, total = 0;
void Check(bool ok, string name)
{
    total++;
    if (ok) passed++;
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
}

Console.WriteLine("=== Eval test harness (deterministic, offline) ===");

// The pre-response EvaluateAsync overload needs an AIAgent for its signature, but
// it scores the responses we hand it. The stub client throws if anything calls it.
ChatClientAgent agent = new(new UninvokedChatClient(), name: "FaqAgentUnderTest");
string[] ptoQuery = ["How many PTO days do new employees get?"];

const string Correct = "New employees (0-2 years of service) receive 15 PTO days per year.";
const string Confabulated = "New employees get 20 PTO days per year.";
const string Bigger = "Employees get 115 days of PTO.";
const string Negated = "Not 15 days; employees get 20 days.";

// ── 1. The failure: substring checks accept both counterexamples ───
Console.WriteLine("\n-- 1. Substring checks (the check the first edition called groundedness) --");
LocalEvaluator substring = new(
    FunctionEvaluator.Create("contains_15", (string response) => response.Contains("15", StringComparison.Ordinal)),
    EvalChecks.ContainsExpected());
foreach (string answer in new[] { Bigger, Negated })
{
    bool ok = await Passes(substring, answer);
    Check(ok, $"DEFECT REPRODUCED: Contains(\"15\") and EvalChecks.ContainsExpected() pass \"{answer}\"");
}

// ── 2. The fix: a value check named for what it measures ───────────
Console.WriteLine("\n-- 2. pto_value_check: every day count stated must be 15, and one must be stated --");
LocalEvaluator valueCheck = new(
    FunctionEvaluator.Create("pto_value_check",
        (string response) => PtoValue.StatesOnly(response, days: 15)));
Check(await Passes(valueCheck, Correct), "the correct answer passes");
Check(!await Passes(valueCheck, Confabulated), "the confabulated \"20 PTO days\" fails");
Check(!await Passes(valueCheck, Bigger), "\"115 days\" fails");
Check(!await Passes(valueCheck, Negated), "\"Not 15 days; employees get 20 days\" fails");

// ── 3. What numRepetitions does (probe against MAF 1.28) ───────────
// A counting client answers every call; two queries with numRepetitions: 3.
Console.WriteLine("\n-- 3. numRepetitions, observed --");
CountingChatClient counter = new(Correct);
ChatClientAgent counted = new(counter, name: "Counted");
AgentEvaluationResults repeated = await counted.EvaluateAsync(
    ["How many PTO days do new employees get?", "And after six years?"], valueCheck, numRepetitions: 3);
Console.WriteLine($"  2 queries x numRepetitions 3: model calls {counter.Calls}, Items {repeated.Items.Count}, " +
                  $"Total {repeated.Total}, Passed {repeated.Passed}");
Check(counter.Calls == 6 && repeated.Items.Count == 6 && repeated.Total == 6,
    "each repetition is its own run and its own item: the results are not averaged per query");

Console.WriteLine($"\nRESULT {passed}/{total}");
return passed == total ? 0 : 1;

// One pre-built response through one evaluator; true when every check passed.
async Task<bool> Passes(LocalEvaluator evaluator, string response)
{
    AgentResponse[] responses = [new AgentResponse(new ChatMessage(ChatRole.Assistant, response))];
    AgentEvaluationResults results = await agent.EvaluateAsync(
        responses, ptoQuery, evaluator, expectedOutput: ["15"]);
    return results.AllPassed;
}

// A chat client that must never be called: the harness scores stored responses.
internal sealed class UninvokedChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("The eval harness must not call the model; it scores stored responses.");

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("The eval harness must not call the model; it scores stored responses.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

// Answers every call with the same text and counts the calls.
internal sealed class CountingChatClient(string reply) : IChatClient
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates()) yield return update;
    }

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
