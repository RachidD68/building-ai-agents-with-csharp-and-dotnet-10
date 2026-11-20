using Microsoft.Extensions.AI;

namespace Ch20.ConsumptionLimits;

public sealed class TokenBudgetExceededException(string message) : Exception(message);

/// <summary>
/// A token budget shared by every call through this client (one tenant, one
/// session: scope it where you create it). Each call first reserves its worst
/// case; the reservation is replaced by the real usage when the call returns.
/// A call whose reservation does not fit is refused before it reaches the model.
/// </summary>
public sealed class TokenBudgetClient(IChatClient inner, long budget, long reservePerCall)
    : DelegatingChatClient(inner)
{
    private readonly Lock _gate = new();
    private long _spent, _reserved;

    public long Spent { get { lock (_gate) return _spent; } }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        long reservation = options?.MaxOutputTokens is int max ? max + EstimateInput(messages) : reservePerCall;
        lock (_gate)
        {
            if (_spent + _reserved + reservation > budget)
                throw new TokenBudgetExceededException(
                    $"Token budget {budget}: {_spent} spent, {_reserved} reserved, {reservation} requested.");
            _reserved += reservation;
        }

        long used = reservation;   // a failed call is charged its reservation
        try
        {
            ChatResponse response = await base.GetResponseAsync(messages, options, cancellationToken);
            used = response.Usage?.TotalTokenCount ?? reservation;
            return response;
        }
        finally
        {
            lock (_gate)
            {
                _reserved -= reservation;
                _spent += used;
            }
        }
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This sample budgets the non-streaming path only.");

    // About four characters per token: a reservation, not a bill.
    private static long EstimateInput(IEnumerable<ChatMessage> messages) =>
        messages.Sum(m => (long)m.Text.Length) / 4 + 1;
}

/// <summary>The first edition's budget check, after the call. Kept only to reproduce its defect.</summary>
public sealed class FirstEditionBudgetClient(IChatClient inner, long budget) : DelegatingChatClient(inner)
{
    public long TokensUsed { get; private set; }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ChatResponse response = await base.GetResponseAsync(messages, options, cancellationToken);
        TokensUsed += response.Usage?.TotalTokenCount ?? 0;
        if (TokensUsed > budget)
            throw new InvalidOperationException($"Token budget of {budget} exceeded (used {TokensUsed}).");
        return response;
    }
}

/// <summary>A model that asks to call the same tool every turn and never stops.</summary>
public sealed class RunawayClient : IChatClient
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        int turn = Interlocked.Increment(ref _calls);
        FunctionCallContent call = new($"call-{turn}", "next_step", new Dictionary<string, object?> { ["step"] = turn });
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>Reports a fixed usage per call, optionally after a delay, and counts calls in flight.</summary>
public sealed class MeteredClient(int tokensPerCall, TimeSpan delay = default) : IChatClient
{
    private int _calls, _inFlight, _peak;
    public int Calls => Volatile.Read(ref _calls);
    public int PeakConcurrency => Volatile.Read(ref _peak);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        int now = Interlocked.Increment(ref _inFlight);
        int peak;
        while (now > (peak = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, now, peak) != peak) { }
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
            {
                Usage = new UsageDetails { TotalTokenCount = tokensPerCall },
            };
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>A model that takes longer than any sane request deadline.</summary>
public sealed class SlowClient(TimeSpan delay) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        await Task.Delay(delay, cancellationToken);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
