using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Ch16.MiddlewarePipeline.Middleware;

/// <summary>
/// Chat client middleware that logs every request and response.
/// Wraps the inner <see cref="IChatClient"/> and delegates after logging.
/// </summary>
/// <remarks>
/// This uses the <see cref="DelegatingChatClient"/> base class from
/// Microsoft.Extensions.AI, which provides the standard middleware pattern.
/// Both <see cref="GetResponseAsync"/> and <see cref="GetStreamingResponseAsync"/>
/// are overridden so the middleware applies on every invocation path.
/// </remarks>
public sealed class LoggingMiddleware : DelegatingChatClient
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new <see cref="LoggingMiddleware"/>.
    /// </summary>
    /// <param name="innerClient">The next client in the pipeline.</param>
    /// <param name="logger">Logger instance for writing structured logs.</param>
    public LoggingMiddleware(IChatClient innerClient, ILogger logger)
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
        ChatMessage? lastMessage = messages.LastOrDefault();
        _logger.LogInformation(
            "[Logging] Request: {MessageCount} message(s), last = \"{LastMessage}\"",
            messages.Count(),
            Truncate(lastMessage?.Text ?? "(empty)", 80));

        ChatResponse response = await base.GetResponseAsync(
            messages, options, cancellationToken);

        _logger.LogInformation(
            "[Logging] Response: {ResponseLength} chars, finish reason = {FinishReason}",
            response.Text?.Length ?? 0,
            response.FinishReason?.ToString() ?? "none");

        return response;
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatMessage? lastMessage = messages.LastOrDefault();
        _logger.LogInformation(
            "[Logging] Streaming request: {MessageCount} message(s), last = \"{LastMessage}\"",
            messages.Count(),
            Truncate(lastMessage?.Text ?? "(empty)", 80));

        int totalChars = 0;
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(
            messages, options, cancellationToken))
        {
            totalChars += update.Text?.Length ?? 0;
            yield return update;
        }

        _logger.LogInformation(
            "[Logging] Streaming response: {ResponseLength} chars total",
            totalChars);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength), "...");
}
