// ============================================================================
// Exercise 05 — Consumption Limits (Challenge)
// ----------------------------------------------------------------------------
// Cap a deliberately looping agent (OWASP LLM10, Unbounded Consumption), then
// enforce PER-SESSION budgets. A deterministic RunawayClient asks to call the
// same tool every turn and never stops; MaximumIterationsPerRequest must stop
// it at EXACTLY the configured cap. A SessionBudgetClient then tracks token
// and request budgets PER SESSION ID (ChatOptions.ConversationId), enforced
// across calls in that session: once a session crosses either limit, every
// further call in that session is SHORT-CIRCUITED with a refusal — no model
// call, no exception — while other sessions run untouched.
//
// Deterministic on purpose: test doubles make each limit provably fire without
// a live model. In production the inner client is your real provider.
// ============================================================================

using System.ComponentModel;
using Microsoft.Extensions.AI;

Console.WriteLine("=== Exercise: Consumption Limits (LLM10) ===\n");

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

Console.WriteLine(toolCalls == IterationCap
    ? $"PROOF: loop stopped at exactly {toolCalls} calls (the cap)."
    : $"NOTE: loop ran {toolCalls} calls vs cap {IterationCap}.");
Console.WriteLine();

// ── Per-session token budget ──────────────────────────────────────────
// The budget is a SESSION ledger, not a per-request check: usage accumulates
// across calls carrying the same session id, and the middleware short-circuits
// the session once its budget is spent. Other sessions are unaffected.
const long TokenBudgetPerSession = 1000;
Console.WriteLine($"-- Per-session token budget = {TokenBudgetPerSession} --");

var tokenBudgeted = new SessionBudgetClient(
    new MeteredClient(), TokenBudgetPerSession, requestBudgetPerSession: int.MaxValue);

// session-a: MeteredClient reports 400 tokens/call, so the ledger crosses the
// budget on call 3 (1,200 total); call 4 is short-circuited before the model.
for (int i = 1; i <= 4; i++)
{
    await RunInSession(tokenBudgeted, "session-a", $"request {i}");
}

// session-b has its OWN ledger — session-a's exhaustion does not leak across.
await RunInSession(tokenBudgeted, "session-b", "request 1");

Console.WriteLine(
    tokenBudgeted.TokensUsed("session-a") >= TokenBudgetPerSession
    && tokenBudgeted.TokensUsed("session-b") < TokenBudgetPerSession
        ? "PROOF: session-a is capped while session-b runs untouched (per-session isolation)."
        : "NOTE: unexpected ledger state — inspect the output above.");
Console.WriteLine();

// ── Per-session request budget (independent guard) ────────────────────
const int RequestBudgetPerSession = 2;
Console.WriteLine($"-- Per-session request budget = {RequestBudgetPerSession} --");

var requestBudgeted = new SessionBudgetClient(
    new MeteredClient(), tokenBudgetPerSession: long.MaxValue, RequestBudgetPerSession);

// session-c: requests 1-2 pass; request 3 is short-circuited by the request
// ledger even though the token ledger has plenty of room.
for (int i = 1; i <= 3; i++)
{
    await RunInSession(requestBudgeted, "session-c", $"request {i}");
}

// session-d again proves isolation: a fresh session id, a fresh ledger.
await RunInSession(requestBudgeted, "session-d", "request 1");

Console.WriteLine(
    requestBudgeted.RequestsMade("session-c") == RequestBudgetPerSession
    && requestBudgeted.RequestsMade("session-d") == 1
        ? "PROOF: session-c stopped at its request budget; session-d unaffected."
        : "NOTE: unexpected ledger state — inspect the output above.");

Console.WriteLine("\n=== Exercise complete! ===");

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
/// calls in that session. Once a session crosses either limit, every further
/// call in that session is short-circuited with a refusal response — no model
/// call, no exception, and no effect on other sessions' ledgers.
/// </summary>
sealed class SessionBudgetClient(
    IChatClient inner, long tokenBudgetPerSession, int requestBudgetPerSession)
    : DelegatingChatClient(inner)
{
    private sealed class Ledger
    {
        public long Tokens;
        public int Requests;
    }

    private readonly Dictionary<string, Ledger> _sessions = new(StringComparer.Ordinal);

    /// <summary>Cumulative tokens recorded against the given session.</summary>
    public long TokensUsed(string sessionId) => GetLedger(sessionId).Tokens;

    /// <summary>Number of requests that reached the model for the given session.</summary>
    public int RequestsMade(string sessionId) => GetLedger(sessionId).Requests;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string sessionId = options?.ConversationId ?? "(no-session)";
        Ledger ledger = GetLedger(sessionId);

        // Short-circuit BEFORE the model call once a limit is already spent.
        if (ledger.Requests >= requestBudgetPerSession)
        {
            return Refusal($"session '{sessionId}' request budget exhausted " +
                           $"({ledger.Requests}/{requestBudgetPerSession})");
        }

        if (ledger.Tokens >= tokenBudgetPerSession)
        {
            return Refusal($"session '{sessionId}' token budget exhausted " +
                           $"({ledger.Tokens}/{tokenBudgetPerSession})");
        }

        ChatResponse response = await base.GetResponseAsync(messages, options, cancellationToken);

        ledger.Requests++;
        ledger.Tokens += response.Usage?.TotalTokenCount ?? 0;
        return response;
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
