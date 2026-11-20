using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Ch15.DurableReview;

/// <summary>A fake IChatClient for --offline and the self-tests: one fixed summary, no network.</summary>
public sealed class ScriptedChatClient(string reply = ScriptedChatClient.Summary) : IChatClient
{
    public const string Summary =
        "Alice claims 64.80 EUR for a taxi to the airport. The receipt total matches the claimed amount.";

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, reply);
        await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>
/// The model for a resumed review: there is none. The summary was written
/// before the checkpoint, so a call here means a step is being replayed.
/// </summary>
public sealed class NoModelChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("a resumed review must not call the model");

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("a resumed review must not call the model");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
