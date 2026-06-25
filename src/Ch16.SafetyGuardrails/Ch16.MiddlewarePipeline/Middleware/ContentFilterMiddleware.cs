using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Ch16.MiddlewarePipeline.Middleware;

/// <summary>
/// Chat client middleware that blocks requests containing prohibited keywords.
/// Acts as a content safety guardrail in the pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Trust boundary: only <see cref="ChatRole.User"/> messages are scanned.
/// System and assistant messages originate inside our own code; scanning them
/// invites false positives without improving safety.
/// </para>
/// <para>
/// Defence-in-depth: every safety middleware MUST override both
/// <see cref="GetResponseAsync"/> and <see cref="GetStreamingResponseAsync"/>.
/// They are independent entry points on <see cref="IChatClient"/>, and any
/// uncovered override falls through to the base, leaving the streaming path
/// unguarded.
/// </para>
/// <para>
/// In production, replace keyword matching with a more sophisticated approach:
/// Azure AI Content Safety API, custom ML classifiers, or semantic similarity
/// against a blocklist.
/// </para>
/// </remarks>
public sealed class ContentFilterMiddleware : DelegatingChatClient
{
    private const string SafeFallbackText =
        "I'm unable to process this request. " +
        "It contains content that violates our usage policy.";

    private readonly ILogger _logger;
    private readonly HashSet<string> _blockedKeywords;

    /// <summary>
    /// Initializes a new <see cref="ContentFilterMiddleware"/>.
    /// </summary>
    /// <param name="innerClient">The next client in the pipeline.</param>
    /// <param name="logger">Logger instance for writing filter events.</param>
    /// <param name="blockedKeywords">Set of keywords that will trigger blocking.</param>
    public ContentFilterMiddleware(
        IChatClient innerClient,
        ILogger logger,
        IEnumerable<string> blockedKeywords)
        : base(innerClient)
    {
        _logger = logger;
        _blockedKeywords = new HashSet<string>(
            blockedKeywords,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string? blockedWord = FindBlockedKeyword(messages);
        if (blockedWord is not null)
        {
            _logger.LogWarning(
                "[ContentFilter] BLOCKED — user message contains prohibited keyword: \"{Keyword}\"",
                blockedWord);

            return new ChatResponse([
                new ChatMessage(ChatRole.Assistant, SafeFallbackText)
            ]);
        }

        _logger.LogInformation("[ContentFilter] PASSED — no blocked content detected");
        return await base.GetResponseAsync(messages, options, cancellationToken);
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? blockedWord = FindBlockedKeyword(messages);
        if (blockedWord is not null)
        {
            _logger.LogWarning(
                "[ContentFilter] BLOCKED (streaming) — user message contains prohibited keyword: \"{Keyword}\"",
                blockedWord);

            yield return new ChatResponseUpdate(ChatRole.Assistant, SafeFallbackText);
            yield break;
        }

        _logger.LogInformation("[ContentFilter] PASSED (streaming) — no blocked content detected");
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(
            messages, options, cancellationToken))
        {
            yield return update;
        }
    }

    /// <summary>
    /// Scans every <see cref="ChatRole.User"/> message individually for a blocked keyword.
    /// Per-message inspection prevents false positives on system/assistant text and
    /// avoids multi-message evasion that a single concatenated string would mask.
    /// </summary>
    /// <param name="messages">The full conversation messages.</param>
    /// <returns>The first blocked keyword found, or <see langword="null"/> if none match.</returns>
    private string? FindBlockedKeyword(IEnumerable<ChatMessage> messages)
    {
        foreach (ChatMessage msg in messages.Where(m => m.Role == ChatRole.User))
        {
            string text = msg.Text ?? string.Empty;
            string? hit = _blockedKeywords.FirstOrDefault(kw =>
                text.Contains(kw, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
            {
                return hit;
            }
        }
        return null;
    }
}
