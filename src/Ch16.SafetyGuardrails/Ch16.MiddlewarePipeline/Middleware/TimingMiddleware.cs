using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Ch16.MiddlewarePipeline.Middleware;

/// <summary>
/// Chat client middleware that measures and logs the latency of each request.
/// Wraps the inner <see cref="IChatClient"/> and records elapsed time.
/// Both <see cref="GetResponseAsync"/> and <see cref="GetStreamingResponseAsync"/>
/// are timed so observability is consistent across invocation paths.
/// </summary>
public sealed class TimingMiddleware : DelegatingChatClient
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new <see cref="TimingMiddleware"/>.
    /// </summary>
    /// <param name="innerClient">The next client in the pipeline.</param>
    /// <param name="logger">Logger instance for writing timing data.</param>
    public TimingMiddleware(IChatClient innerClient, ILogger logger)
        : base(innerClient)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        ChatResponse response = await base.GetResponseAsync(
            messages, options, cancellationToken);

        sw.Stop();

        _logger.LogInformation(
            "[Timing] Request completed in {ElapsedMs:F1} ms",
            sw.Elapsed.TotalMilliseconds);

        return response;
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(
            messages, options, cancellationToken))
        {
            yield return update;
        }

        sw.Stop();

        _logger.LogInformation(
            "[Timing] Streaming request completed in {ElapsedMs:F1} ms",
            sw.Elapsed.TotalMilliseconds);
    }
}
