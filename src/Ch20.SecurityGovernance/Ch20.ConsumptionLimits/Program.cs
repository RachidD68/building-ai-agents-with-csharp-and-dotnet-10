// ============================================================================
// Chapter 16 — Unbounded Consumption: denial-of-wallet & runaway loops (LLM10)
// ----------------------------------------------------------------------------
// Cost and availability are SAFETY concerns. An attacker (or a buggy tool loop)
// can drive unbounded token spend, or an effectively infinite function-call
// loop, until the bill or the rate limit takes the service down. The agent loop
// needs hard caps.
//
// MAF / Microsoft.Extensions.AI give you built-in caps on the
// FunctionInvokingChatClient:
//   - MaximumIterationsPerRequest      — caps function-call roundtrips (default 40)
//   - MaximumConsecutiveErrorsPerRequest — caps consecutive failing calls (default 3)
//   - ChatOptions.AllowMultipleToolCalls = false — one tool call per iteration
// Plus two patterns you add yourself:
//   - a per-request deadline via CancellationToken
//   - a per-request token-budget guard (a tiny DelegatingChatClient)
//
// To make the cap provably fire (a small local model rarely loops on demand),
// this demo uses a deterministic RunawayClient that requests the same tool every
// turn and never stops. The cap stops it cleanly — no exception, no infinite
// spin. (API-gateway rate limiting lives in Chapter 13; this is the AGENT LOOP.)
// ============================================================================

using System.ComponentModel;
using Microsoft.Extensions.AI;

Console.WriteLine("=== Chapter 16: Unbounded Consumption (LLM10) ===");
Console.WriteLine();

// ── A tool the runaway model keeps calling forever ────────────────────
int toolCalls = 0;

[Description("Returns the next migration step. Always reports more work to do.")]
string NextStep([Description("the current step number")] int step)
{
    toolCalls++;
    Console.WriteLine($"   [tool] next_step(step={step}) — call #{toolCalls}");
    return $"Step {step} done. Call next_step with step={step + 1}.";
}

AIFunction loopTool = AIFunctionFactory.Create((Delegate)NextStep, name: "next_step");

// ── Demo 1: the iteration cap terminates a runaway loop ───────────────
Console.WriteLine("-- Demo 1: MaximumIterationsPerRequest caps a runaway loop --");
const int IterationCap = 4;

var runaway = new RunawayClient();
IChatClient capped = runaway
    .AsBuilder()
    .UseFunctionInvocation(configure: c => c.MaximumIterationsPerRequest = IterationCap)
    .Build();

var options = new ChatOptions
{
    Tools = [loopTool],
    // One tool call per iteration: a runaway model cannot fan out within a turn.
    AllowMultipleToolCalls = false
};

Console.WriteLine($"   cap = {IterationCap} iterations");
ChatResponse capResult = await capped.GetResponseAsync(
    [new ChatMessage(ChatRole.User, "Run the migration to completion.")], options);

Console.WriteLine($"   loop terminated cleanly after {toolCalls} tool calls " +
                  $"(model wanted to continue forever).");
Console.WriteLine();

// ── Demo 2: a per-request token-budget guard ──────────────────────────
// A tiny middleware that sums token usage across the request and throws once a
// budget is exceeded — denial-of-wallet protection independent of the loop cap.
Console.WriteLine("-- Demo 2: a per-request token-budget guard --");
const long TokenBudget = 1000;

var budgeted = new TokenBudgetClient(new MeteredClient(), TokenBudget);
try
{
    // MeteredClient reports 400 tokens per call; the 3rd call trips the 1000 budget.
    for (int i = 1; i <= 4; i++)
    {
        ChatResponse r = await budgeted.GetResponseAsync(
            [new ChatMessage(ChatRole.User, $"request {i}")]);
        Console.WriteLine($"   request {i}: ok — cumulative tokens = {budgeted.TokensUsed}");
    }
}
catch (InvalidOperationException ex)
{
    Console.WriteLine($"   BLOCKED: {ex.Message}");
}
Console.WriteLine();

// ── Demo 3: a per-request deadline via CancellationToken ──────────────
Console.WriteLine("-- Demo 3: a per-request deadline (CancellationToken) --");
using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
try
{
    var slow = new SlowClient(TimeSpan.FromSeconds(5));
    await slow.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: deadline.Token);
    Console.WriteLine("   completed (unexpected)");
}
catch (OperationCanceledException)
{
    Console.WriteLine("   request cancelled at the 200ms deadline — no runaway wait.");
}
Console.WriteLine();

Console.WriteLine("=== Done ===");

// ----------------------------------------------------------------------------
// Test doubles — deterministic stand-ins so every cap is provably exercised
// without a live model. In production the inner client is your real provider.
// ----------------------------------------------------------------------------

/// <summary>A model that asks to call the same tool every turn and never stops.</summary>
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

/// <summary>Reports a fixed token usage per call so the budget guard can be tested.</summary>
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

/// <summary>A delegating client that enforces a cumulative token budget.</summary>
sealed class TokenBudgetClient(IChatClient inner, long budget) : DelegatingChatClient(inner)
{
    public long TokensUsed { get; private set; }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ChatResponse response = await base.GetResponseAsync(messages, options, cancellationToken);
        TokensUsed += response.Usage?.TotalTokenCount ?? 0;
        if (TokensUsed > budget)
        {
            throw new InvalidOperationException(
                $"Token budget of {budget} exceeded (used {TokensUsed}).");
        }
        return response;
    }
}

/// <summary>A model that takes longer than any sane request deadline.</summary>
sealed class SlowClient(TimeSpan delay) : IChatClient
{
    public ChatClientMetadata Metadata { get; } = new("slow");

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(delay, cancellationToken);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
