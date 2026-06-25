// ============================================================================
// Exercise 05 — Consumption Limits (Intermediate)
// ----------------------------------------------------------------------------
// Cap a deliberately looping agent (OWASP LLM10, Unbounded Consumption). A
// deterministic RunawayClient asks to call the same tool every turn and never
// stops. MaximumIterationsPerRequest must stop it at EXACTLY the configured cap.
// A per-request token budget then trips independently of the iteration cap.
//
// Deterministic on purpose: a real model rarely loops on demand, so a test
// double makes the cap provably fire. In production the inner client is your
// real provider.
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

// ── Token budget (independent guard) ──────────────────────────────────
const long TokenBudget = 1000;
Console.WriteLine($"-- Token budget = {TokenBudget} --");
var budgeted = new TokenBudgetClient(new MeteredClient(), TokenBudget);
try
{
    for (int i = 1; i <= 4; i++)
    {
        await budgeted.GetResponseAsync([new ChatMessage(ChatRole.User, $"request {i}")]);
        Console.WriteLine($"   request {i}: ok — cumulative tokens = {budgeted.TokensUsed}");
    }
}
catch (InvalidOperationException ex)
{
    Console.WriteLine($"   BLOCKED: {ex.Message}");
}

Console.WriteLine("\n=== Exercise complete! ===");

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
            throw new InvalidOperationException(
                $"Token budget of {budget} exceeded (used {TokensUsed}).");
        return response;
    }
}
