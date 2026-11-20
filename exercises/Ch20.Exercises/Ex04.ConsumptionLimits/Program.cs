// ============================================================================
// Exercise 04 — Consumption Limits (Challenge)
// ----------------------------------------------------------------------------
// Cap a deliberately looping agent (OWASP LLM10, Unbounded Consumption), then
// enforce PER-SESSION budgets. A deterministic RunawayClient asks to call the
// same tool every turn and never stops; MaximumIterationsPerRequest must stop
// it at EXACTLY the configured cap. A SessionBudgetClient then tracks token
// and request budgets PER SESSION ID (ChatOptions.ConversationId), enforced
// across calls in that session. Each call RESERVES its worst case before it
// reaches the model; a call whose reservation does not fit is SHORT-CIRCUITED
// with a refusal — no model call, no exception — so a session never spends
// past its budget, while other sessions run untouched.
//
// Deterministic on purpose: test doubles make each limit provably fire without
// a live model. In production the inner client is your real provider.
//   dotnet run -- --self-test     prints RESULT n/n, exit code 1 on any failure
// ============================================================================

using System.ComponentModel;
using Microsoft.Extensions.AI;

Console.WriteLine(args.Contains("--self-test")
    ? "=== Exercise: Consumption Limits (LLM10): self-test ===\n"
    : "=== Exercise: Consumption Limits (LLM10) (no model; the same suite runs with --self-test) ===\n");
int passed = 0, total = 0;
void Expect(string name, bool ok)
{
    total++;
    if (ok) passed++;
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
}

int toolCalls = 0;

[Description("Returns the next migration step. Always reports more work to do.")]
string NextStep([Description("the current step number")] int step)
{
    toolCalls++;
    Console.WriteLine($"   [tool] next_step(step={step}) — call #{toolCalls}");
    return $"Step {step} done. Call next_step with step={step + 1}.";
}

AIFunction loopTool = AIFunctionFactory.Create((Delegate)NextStep, name: "next_step");

// ── Iteration cap ─────────────────────────────────────────────────────
const int IterationCap = 3;
Console.WriteLine($"-- Iteration cap = {IterationCap} --");

IChatClient capped = new RunawayClient()
    .AsBuilder()
    .UseFunctionInvocation(configure: c => c.MaximumIterationsPerRequest = IterationCap)
    .Build();

var options = new ChatOptions { Tools = [loopTool], AllowMultipleToolCalls = false };
await capped.GetResponseAsync(
    [new ChatMessage(ChatRole.User, "Run the migration to completion.")], options);

Expect($"loop stopped at {toolCalls} tool calls (cap {IterationCap})", toolCalls == IterationCap);
Console.WriteLine();

// ── Per-session token budget ──────────────────────────────────────────
// The budget is a SESSION ledger, not a per-request check: usage accumulates
// across calls carrying the same session id. Each call first reserves its worst
// case (here 400 tokens, what one call can cost); the reservation becomes the
// real usage when the call returns. A call that does not fit never starts, so
// the check comes BEFORE the spend, not after it. Other sessions are unaffected.
const long TokenBudgetPerSession = 1000;
const long ReservePerCall = 400;
Console.WriteLine($"-- Per-session token budget = {TokenBudgetPerSession}, reserve {ReservePerCall} per call --");

var tokenBudgeted = new SessionBudgetClient(
    new MeteredClient(), TokenBudgetPerSession, requestBudgetPerSession: int.MaxValue, ReservePerCall);

// session-a: calls 1-2 spend 800; call 3 would need 400 more (1,200 > 1,000),
// so it is refused before the model, and so is call 4.
for (int i = 1; i <= 4; i++)
{
    await RunInSession(tokenBudgeted, "session-a", $"request {i}");
}

// session-b has its OWN ledger — session-a's exhaustion does not leak across.
await RunInSession(tokenBudgeted, "session-b", "request 1");

Expect($"session-a spent {tokenBudgeted.TokensUsed("session-a")} of {TokenBudgetPerSession} in {tokenBudgeted.RequestsMade("session-a")} calls; " +
       $"session-b untouched ({tokenBudgeted.TokensUsed("session-b")})",
    tokenBudgeted.TokensUsed("session-a") <= TokenBudgetPerSession
    && tokenBudgeted.RequestsMade("session-a") == 2
    && tokenBudgeted.TokensUsed("session-b") == 400);
Console.WriteLine();

// ── Per-session request budget (independent guard) ────────────────────
const int RequestBudgetPerSession = 2;
Console.WriteLine($"-- Per-session request budget = {RequestBudgetPerSession} --");

var requestBudgeted = new SessionBudgetClient(
    new MeteredClient(), tokenBudgetPerSession: long.MaxValue, RequestBudgetPerSession, reservePerCall: 400);

// session-c: requests 1-2 pass; request 3 is short-circuited by the request
// ledger even though the token ledger has plenty of room.
for (int i = 1; i <= 3; i++)
{
    await RunInSession(requestBudgeted, "session-c", $"request {i}");
}

// session-d again proves isolation: a fresh session id, a fresh ledger.
await RunInSession(requestBudgeted, "session-d", "request 1");

Expect($"session-c stopped at {requestBudgeted.RequestsMade("session-c")} requests; session-d made {requestBudgeted.RequestsMade("session-d")}",
    requestBudgeted.RequestsMade("session-c") == RequestBudgetPerSession
    && requestBudgeted.RequestsMade("session-d") == 1);

Console.WriteLine($"\nRESULT {passed}/{total}");
return passed == total ? 0 : 1;

/// <summary>Sends one request tagged with a session id and prints the outcome.</summary>
static async Task RunInSession(SessionBudgetClient client, string sessionId, string prompt)
{
    // ConversationId is the session key the middleware buckets budgets by.
    ChatResponse response = await client.GetResponseAsync(
        [new ChatMessage(ChatRole.User, prompt)],
        new ChatOptions { ConversationId = sessionId });

    Console.WriteLine($"   [{sessionId}] {prompt} -> {response.Text} " +
                      $"(session totals: {client.TokensUsed(sessionId)} tokens, " +
                      $"{client.RequestsMade(sessionId)} requests)");
}

/// <summary>Asks to call the same tool every turn and never stops.</summary>
sealed class RunawayClient : IChatClient
{
    private int _turn;
    public ChatClientMetadata Metadata { get; } = new("runaway");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _turn++;
        var call = new FunctionCallContent(
            $"call-{_turn}", "next_step",
            new Dictionary<string, object?> { ["step"] = _turn });
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>Reports a fixed token usage per call so the budget guards can be tested.</summary>
sealed class MeteredClient : IChatClient
{
    public ChatClientMetadata Metadata { get; } = new("metered");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
        {
            Usage = new UsageDetails { InputTokenCount = 300, OutputTokenCount = 100, TotalTokenCount = 400 }
        });

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>
/// Middleware that enforces PER-SESSION token and request budgets. Budgets are
/// keyed on ChatOptions.ConversationId (the session id) and accumulate across
/// calls in that session. Every call reserves its worst case (reservePerCall
/// tokens and one request) under a lock BEFORE it reaches the model; a call
/// whose reservation does not fit is short-circuited with a refusal — no model
/// call, no exception, and no effect on other sessions' ledgers. When the call
/// returns, the token reservation is replaced by the reported usage.
/// </summary>
sealed class SessionBudgetClient(
    IChatClient inner, long tokenBudgetPerSession, int requestBudgetPerSession, long reservePerCall)
    : DelegatingChatClient(inner)
{
    private sealed class Ledger
    {
        public long Tokens, Reserved;
        public int Requests;
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Ledger> _sessions = new(StringComparer.Ordinal);

    /// <summary>Cumulative tokens recorded against the given session.</summary>
    public long TokensUsed(string sessionId) { lock (_gate) return GetLedger(sessionId).Tokens; }

    /// <summary>Number of requests that reached the model for the given session.</summary>
    public int RequestsMade(string sessionId) { lock (_gate) return GetLedger(sessionId).Requests; }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string sessionId = options?.ConversationId ?? "(no-session)";
        Ledger ledger;

        // Reserve BEFORE the model call: the check and the reservation are one step.
        lock (_gate)
        {
            ledger = GetLedger(sessionId);
            if (ledger.Requests >= requestBudgetPerSession)
            {
                return Refusal($"session '{sessionId}' request budget exhausted " +
                               $"({ledger.Requests}/{requestBudgetPerSession})");
            }

            if (ledger.Tokens + ledger.Reserved + reservePerCall > tokenBudgetPerSession)
            {
                return Refusal($"session '{sessionId}' token budget would be exceeded " +
                               $"({ledger.Tokens} spent + {reservePerCall} for this call > {tokenBudgetPerSession})");
            }

            ledger.Requests++;
            ledger.Reserved += reservePerCall;
        }

        long used = reservePerCall;   // a failed call is charged its reservation
        try
        {
            ChatResponse response = await base.GetResponseAsync(messages, options, cancellationToken);
            used = response.Usage?.TotalTokenCount ?? reservePerCall;
            return response;
        }
        finally
        {
            lock (_gate)
            {
                ledger.Reserved -= reservePerCall;
                ledger.Tokens += used;
            }
        }
    }

    private Ledger GetLedger(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out Ledger? ledger))
        {
            ledger = new Ledger();
            _sessions[sessionId] = ledger;
        }
        return ledger;
    }

    private static ChatResponse Refusal(string reason) =>
        new(new ChatMessage(ChatRole.Assistant, $"[budget] {reason}. Request refused."));
}
