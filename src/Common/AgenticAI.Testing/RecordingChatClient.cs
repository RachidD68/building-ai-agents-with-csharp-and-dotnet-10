using Microsoft.Extensions.AI;

namespace AgenticAI.Testing;

/// <summary>
/// Wraps a real <see cref="IChatClient"/> and records what each call sent, so a
/// live run can check the same contracts as the fake: which instructions and
/// messages reached the provider. It changes nothing on the way through.
/// </summary>
public sealed class RecordingChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    private readonly List<FakeCall> _calls = [];
    private readonly Lock _gate = new();

    /// <summary>Every call so far, in order.</summary>
    public IReadOnlyList<FakeCall> Calls { get { lock (_gate) return [.. _calls]; } }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        List<ChatMessage> sent = [.. messages];
        Record(sent, options, streaming: false, cancellationToken);
        return base.GetResponseAsync(sent, options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        List<ChatMessage> sent = [.. messages];
        Record(sent, options, streaming: true, cancellationToken);
        return base.GetStreamingResponseAsync(sent, options, cancellationToken);
    }

    private void Record(List<ChatMessage> sent, ChatOptions? options, bool streaming, CancellationToken token)
    {
        lock (_gate) _calls.Add(new FakeCall([.. sent], options?.Clone(), streaming, token));
    }
}
