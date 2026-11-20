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
        CancellationToken cancellationToken = default)
    {
        string prompt = messages.Last(m => m.Role == ChatRole.User).Text;
        bool hard = looksHard(prompt);
        lock (Decisions) Decisions.Add((prompt, hard));
        return hard ? base.GetResponseAsync(messages, options, cancellationToken)
                    : cheap.GetResponseAsync(messages, options, cancellationToken);
    }
}

/// <summary>
/// GA ordered failover, written by hand: the primary is the inner client; when
/// it fails, the fallback answers. A cancellation by the caller is not a
/// failure and is never failed over. Non-streaming only, so no partial output
/// can be exposed before the switch. The experimental OrderedFailoverChatClient
/// (MEAI001) has the same contract and also handles streaming.
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
