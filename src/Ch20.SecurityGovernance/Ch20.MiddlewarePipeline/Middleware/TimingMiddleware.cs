using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Ch20.MiddlewarePipeline.Middleware;

/// <summary>
/// Measures how long the rest of the pipeline takes. Placed innermost, it times
/// the model call alone; a request the content filter blocks never reaches it.
/// </summary>
public sealed class TimingMiddleware(IChatClient innerClient, ILogger logger)
    : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        long start = Stopwatch.GetTimestamp();
        ChatResponse response = await base.GetResponseAsync(messages, options, cancellationToken);
        logger.LogInformation("[Timing] Request completed in {ElapsedMs:F1} ms",
            Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        long start = Stopwatch.GetTimestamp();
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(
            messages, options, cancellationToken))
        {
            yield return update;
        }
        logger.LogInformation("[Timing] Streaming request completed in {ElapsedMs:F1} ms",
            Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }
}
