using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Polly;
using Polly.Retry;

namespace Ch22.ModelRouting;

/// <summary>
/// GA difficulty router: a DelegatingChatClient whose inner client is the
/// capable (default) model; a request the classifier calls easy goes to the
/// cheaper one instead. Every decision is recorded, so misroutes can be
/// counted against the task's true difficulty.
/// </summary>
public sealed class DifficultyRouterChatClient(IChatClient capable, IChatClient cheap, Func<string, bool> looksHard)
    : DelegatingChatClient(capable)
{
    public List<(string Prompt, bool RoutedHard)> Decisions { get; } = [];

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        RoutesHard(messages) ? base.GetResponseAsync(messages, options, cancellationToken)
                             : cheap.GetResponseAsync(messages, options, cancellationToken);

    // Without this override DelegatingChatClient would send every streaming call to the capable tier, unrecorded.
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        RoutesHard(messages) ? base.GetStreamingResponseAsync(messages, options, cancellationToken)
                             : cheap.GetStreamingResponseAsync(messages, options, cancellationToken);

    bool RoutesHard(IEnumerable<ChatMessage> messages)
    {
        string prompt = messages.Last(m => m.Role == ChatRole.User).Text;
        bool hard = looksHard(prompt);
        lock (Decisions) Decisions.Add((prompt, hard));
        return hard;
    }
}

/// <summary>
/// GA ordered failover, written by hand: the primary is the inner client; when
/// it fails, the fallback answers. A cancellation by the caller is not a
/// failure and is never failed over. Streaming fails over only until the first
/// update has reached the caller; after that a failure propagates, so no second
/// generation is spliced onto a partial answer. That is the contract of the
/// experimental OrderedFailoverChatClient (MEAI001), for both calls.
/// </summary>
public sealed class GaFailoverChatClient(IChatClient primary, IChatClient fallback) : DelegatingChatClient(primary)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        List<ChatMessage> history = [.. messages];   // enumerate once: both attempts send the same messages
        try
        {
            return await base.GetResponseAsync(history, options, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return await fallback.GetResponseAsync(history, options, cancellationToken);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<ChatMessage> history = [.. messages];
        IAsyncEnumerator<ChatResponseUpdate>? first = null;
        bool hasUpdate = false, failOver = false;
        try
        {
            first = base.GetStreamingResponseAsync(history, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
            hasUpdate = await first.MoveNextAsync();   // nothing has reached the caller yet
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            failOver = true;
        }
        if (failOver)
        {
            if (first is not null) await first.DisposeAsync();
            await foreach (ChatResponseUpdate update in fallback.GetStreamingResponseAsync(history, options, cancellationToken))
                yield return update;
            yield break;
        }
        await using (first!)
        {
            while (hasUpdate)
            {
                yield return first!.Current;
                hasUpdate = await first.MoveNextAsync();   // output is exposed: a failure from here on propagates
            }
        }
    }
}

/// <summary>A retry inside one tier (Polly via Microsoft.Extensions.Resilience). Put it here
/// AND under a failover, and the two retry owners multiply each other's attempts.</summary>
public sealed class RetryingChatClient(IChatClient inner, int maxRetries) : DelegatingChatClient(inner)
{
    readonly ResiliencePipeline _retry = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions { MaxRetryAttempts = maxRetries, Delay = TimeSpan.Zero,
                                             ShouldHandle = new PredicateBuilder().Handle<HttpRequestException>() })
        .Build();

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync(ct => new ValueTask<ChatResponse>(base.GetResponseAsync(messages, options, ct)), cancellationToken).AsTask();
}

/// <summary>Records every attempt that reached a model: which one, its usage, whether it failed.</summary>
public sealed class MeteredChatClient(IChatClient inner, string model, List<Attempt> log) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            ChatResponse response = await base.GetResponseAsync(messages, options, cancellationToken);
            lock (log) log.Add(new(model, response.Usage, null));
            return response;
        }
        catch (Exception ex)
        {
            lock (log) log.Add(new(model, null, ex.GetType().Name));
            throw;
        }
    }
}

public sealed record Attempt(string Model, UsageDetails? Usage, string? Error);
