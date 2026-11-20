using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Ch01.FirstGlimpse;

/// <summary>
/// A stand-in for a real model when none is configured yet (--offline) and for
/// the self-test. It never calls the network: every answer is the same canned,
/// clearly labelled sentence. It records what the agent sent so the self-test
/// can check the shape of each call.
/// </summary>
internal sealed class OfflineChatClient : IChatClient
{
    public const string CannedReply =
        "[offline: canned reply, no model was called] An AI agent is a program that uses a model " +
        "to decide its next step toward a goal, within limits the application sets.";

    private readonly List<(List<ChatMessage> Messages, ChatOptions? Options)> _calls = [];

    /// <summary>Each call the agent made: the messages it sent and its options.</summary>
    public IReadOnlyList<(List<ChatMessage> Messages, ChatOptions? Options)> Calls => _calls;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _calls.Add(([.. messages], options));
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, CannedReply)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
