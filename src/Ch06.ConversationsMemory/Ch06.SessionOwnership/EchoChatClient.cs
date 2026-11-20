using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Ch06.SessionOwnership;

/// <summary>
/// A stand-in model with no network: it answers with the number of messages it
/// was sent and the last user line, so a test can see what history reached it.
/// </summary>
public sealed class EchoChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        List<ChatMessage> sent = [.. messages];
        string last = sent.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        return Task.FromResult(new ChatResponse(
            new ChatMessage(ChatRole.Assistant, $"[{sent.Count} messages seen] noted: {last}")));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates()) yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
