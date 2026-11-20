using Microsoft.Extensions.AI;

namespace Ch02.CapabilityProfile;

/// <summary>
/// A fake IChatClient that plays a script, one step per call, and records the
/// options it received. A step returns a response or throws. The first-agent
/// chapter builds this kind of fake step by step; here it only feeds the profiler.
/// </summary>
public sealed class ScriptedChatClient(params Func<ChatOptions?, ChatResponse>[] steps) : IChatClient
{
    int _next;

    public List<ChatOptions?> OptionsSeen { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OptionsSeen.Add(options);
        Func<ChatOptions?, ChatResponse> step = steps[_next++ % steps.Length];
        return Task.FromResult(step(options));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The profiler does not stream.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    /// <summary>A reply with a finish reason and, optionally, usage.</summary>
    public static ChatResponse Reply(string text, ChatFinishReason finish, long? output = null, long? reasoning = null) =>
        new(new ChatMessage(ChatRole.Assistant, text))
        {
            FinishReason = finish,
            Usage = output is null ? null : new UsageDetails { InputTokenCount = 120, OutputTokenCount = output, ReasoningTokenCount = reasoning },
        };
}
