using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgenticAI.Testing;

/// <summary>What the scripted client sees on one call: the messages, the options and the call number.</summary>
public sealed record ScriptedTurn(int Call, IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)
{
    /// <summary>The names of the tools the caller advertised on this call.</summary>
    public IReadOnlyList<string> ToolNames =>
        [.. (Options?.Tools ?? []).Select(t => t.Name)];

    /// <summary>The function results already in the conversation, newest last.</summary>
    public IReadOnlyList<FunctionResultContent> Results =>
        [.. Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()];
}

/// <summary>
/// A deterministic <see cref="IChatClient"/> that can ask for tool calls. A script
/// function decides each reply from what the call carried, so a test can drive the
/// real agent loop (function invocation, approvals, context providers) with no model.
/// Every call is recorded. It proves wiring, never the quality of an answer.
/// </summary>
public sealed class ScriptedToolChatClient(Func<ScriptedTurn, ChatResponse> script) : IChatClient
{
    private readonly List<ScriptedTurn> _turns = [];
    private readonly Lock _gate = new();
    private int _callIds;

    /// <summary>Every call so far, in order.</summary>
    public IReadOnlyList<ScriptedTurn> Turns { get { lock (_gate) return [.. _turns]; } }

    /// <summary>A reply that asks for one tool call.</summary>
    public ChatResponse Call(string toolName, IDictionary<string, object?>? arguments = null) =>
        new(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent($"call_{Interlocked.Increment(ref _callIds)}", toolName, arguments)]));

    /// <summary>A plain text reply that ends the turn.</summary>
    public static ChatResponse Say(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text)) { FinishReason = ChatFinishReason.Stop };

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ScriptedTurn turn;
        lock (_gate)
        {
            turn = new ScriptedTurn(_turns.Count + 1, [.. messages], options?.Clone());
            _turns.Add(turn);
        }
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return script(turn);
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
