using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using Ch22.LoadProfile;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;

namespace Ch22.ModelRouting;

/// <summary>
/// A scripted model tier. It answers a task correctly when <c>solves</c> says
/// so (otherwise with the other label), takes a fixed time on the manual
/// clock, reports fixed usage, and can be made to fail with a 429 on every
/// k-th call. Same script, same numbers, every run.
/// </summary>
public sealed class TierModel(ManualClock clock, int latencyMs, UsageDetails usage,
    IReadOnlyDictionary<string, RoutedTask> tasks, Func<RoutedTask, bool> solves, int failEvery = 0) : IChatClient
{
    int _calls;
    public void Reset() => _calls = 0;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RoutedTask task = tasks[messages.Last(m => m.Role == ChatRole.User).Text];
        if (failEvery > 0 && Interlocked.Increment(ref _calls) % failEvery == 0)
        {
            clock.Advance(100);
            throw new HttpRequestException("scripted throttling", null, HttpStatusCode.TooManyRequests);
        }
        clock.Advance(latencyMs);
        string answer = solves(task) ? task.Expected : task.Expected == "APPROVE" ? "ESCALATE" : "APPROVE";
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = usage.InputTokenCount, OutputTokenCount = usage.OutputTokenCount,
                ReasoningTokenCount = usage.ReasoningTokenCount,
            },
        });
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>Streams one update, then fails: output is already exposed when the error arrives.</summary>
public sealed class FailsMidStream : IChatClient
{
    public int Calls;
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Calls);
        yield return new ChatResponseUpdate(ChatRole.Assistant, "APP");
        await Task.Yield();
        throw new HttpRequestException("connection reset mid-stream");
    }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>Enough of IDistributedCache for DistributedCachingChatClient, kept in a dictionary.</summary>
public sealed class DictionaryCache : IDistributedCache
{
    readonly ConcurrentDictionary<string, byte[]> _items = new();
    public byte[]? Get(string key) => _items.TryGetValue(key, out byte[]? v) ? v : null;
    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _items[key] = value;
    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        Set(key, value, options);
        return Task.CompletedTask;
    }
    public void Refresh(string key) { }
    public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
    public void Remove(string key) => _items.TryRemove(key, out _);
    public Task RemoveAsync(string key, CancellationToken token = default)
    {
        Remove(key);
        return Task.CompletedTask;
    }
}
