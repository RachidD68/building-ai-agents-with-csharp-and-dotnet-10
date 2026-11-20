using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Ch20.MiddlewarePipeline.Middleware;

/// <summary>
/// Logs the shape of every request and response: message count, roles,
/// character counts and the finish reason. It never logs message text. A
/// prompt can carry an email address, a card number or a pasted contract, and
/// a log line is copied to more places, and kept longer, than the conversation
/// it came from.
/// </summary>
public sealed class LoggingMiddleware(IChatClient innerClient, ILogger logger)
    : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        List<ChatMessage> sent = [.. messages];
        logger.LogInformation("[Logging] Request: {Shape}", Shape(sent));

        ChatResponse response = await base.GetResponseAsync(sent, options, cancellationToken);

        logger.LogInformation(
            "[Logging] Response: {ResponseLength} chars, finish reason = {FinishReason}",
            response.Text.Length,
            response.FinishReason?.ToString() ?? "none");
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<ChatMessage> sent = [.. messages];
        logger.LogInformation("[Logging] Streaming request: {Shape}", Shape(sent));

        int totalChars = 0;
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(
            sent, options, cancellationToken))
        {
            totalChars += update.Text.Length;
            yield return update;
        }

        logger.LogInformation("[Logging] Streaming response: {ResponseLength} chars total", totalChars);
    }

    /// <summary>"3 message(s) [system, user, user], 212 chars": no content.</summary>
    public static string Shape(IReadOnlyList<ChatMessage> messages) =>
        $"{messages.Count} message(s) [{string.Join(", ", messages.Select(m => m.Role.Value))}], " +
        $"{messages.Sum(m => m.Text.Length)} chars";
}
