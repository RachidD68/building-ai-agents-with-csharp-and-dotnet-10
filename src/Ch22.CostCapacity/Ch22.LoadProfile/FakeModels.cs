using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Ch22.LoadProfile;

/// <summary>
/// Offline stand-in for a provider: a fixed time to first token and
/// generation time, a provider-side capacity beyond which it answers 429, and
/// optionally a connector that ignores cancellation. It answers with the
/// label the task expects unless told otherwise, and reports usage.
/// </summary>
public sealed class ScriptedModel(TimeProvider clock, TimeSpan ttft, TimeSpan generation,
    int capacity = int.MaxValue, bool ignoreCancellation = false, Func<string, string>? answer = null) : IChatClient
{
    int _inFlight, _completed, _throttled;
    public int Completed => Volatile.Read(ref _completed);
    public int Throttled => Volatile.Read(ref _throttled);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string prompt = messages.Last().Text;
        if (Interlocked.Increment(ref _inFlight) > capacity)
        {
            Interlocked.Decrement(ref _inFlight);
            Interlocked.Increment(ref _throttled);
            throw new HttpRequestException("scripted throttling", null, HttpStatusCode.TooManyRequests);
        }
        try
        {
            CancellationToken ct = ignoreCancellation ? CancellationToken.None : cancellationToken;
            await Task.Delay(ttft, clock, ct);
            yield return new ChatResponseUpdate(ChatRole.Assistant, answer?.Invoke(prompt) ?? Label(prompt));
            await Task.Delay(generation, clock, ct);
            Interlocked.Increment(ref _completed);   // the provider did (and would bill) the work
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new UsageContent(new UsageDetails { InputTokenCount = prompt.Length / 4, OutputTokenCount = 2 })]);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    // The bundled workloads end each prompt with the policy verdict a careful reader would reach.
    static string Label(string prompt) => prompt.Contains("within policy", StringComparison.OrdinalIgnoreCase) ? "APPROVE" : "ESCALATE";

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        await GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>
/// A model the test steps by hand: each call waits for the test to release
/// its first token, then its end. Every step is acknowledged, so the test
/// moves the manual clock only after the runner has read it.
/// </summary>
public sealed class GatedModel : IChatClient
{
    public sealed class Call
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource End { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    readonly Queue<Call> _calls;
    public GatedModel(params Call[] calls) => _calls = new(calls);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Call call;
        lock (_calls) call = _calls.Dequeue();
        call.Started.SetResult();
        await call.First.Task;
        yield return new ChatResponseUpdate(ChatRole.Assistant, "APPROVE");
        call.FirstSeen.SetResult();          // runs only after the runner took the update
        await call.End.Task;
        yield return new ChatResponseUpdate(ChatRole.Assistant,
            [new UsageContent(new UsageDetails { InputTokenCount = 40, OutputTokenCount = 1 })]);
    }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>A clock that moves only when the test says so.</summary>
public sealed class ManualClock : TimeProvider
{
    long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public void Advance(int ms) => Interlocked.Add(ref _ticks, TimeSpan.FromMilliseconds(ms).Ticks);
}
