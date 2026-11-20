using Microsoft.Extensions.AI;

namespace Ch05.RobustExtraction;

/// <summary>
/// A fake <see cref="IChatClient"/> for offline tests: it answers each call with
/// the next canned reply (the last one repeats) and records what it was sent.
/// No network, no model, no key. The typed-response samples and exercises that
/// reference this project use it for their --self-test modes.
/// </summary>
public sealed class CannedChatClient(params (string Text, ChatFinishReason Reason)[] replies) : IChatClient
{
    private int _calls;

    /// <summary>How many times the client was called.</summary>
    public int Calls => _calls;

    /// <summary>The response format of the last call, exactly as the agent sent it.</summary>
    public ChatResponseFormat? LastResponseFormat { get; private set; }

    /// <summary>A reply that finished normally.</summary>
    public static (string, ChatFinishReason) Stop(string text) => (text, ChatFinishReason.Stop);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        int call = Interlocked.Increment(ref _calls) - 1;
        LastResponseFormat = options?.ResponseFormat;
        (string text, ChatFinishReason reason) = replies[Math.Min(call, replies.Length - 1)];
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { FinishReason = reason });
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The canned client answers non-streaming calls only.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
