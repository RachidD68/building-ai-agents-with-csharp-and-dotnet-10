using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Ch18.SseStatusStream;

/// <summary>
/// A stand-in model with no network. The first call asks for the
/// <c>get_review_limit</c> tool; once the tool result is in the history, it
/// streams <see cref="Reply"/> word by word. Its text says "submitted": the
/// words a real model writes too, and exactly what a UI must not promote to
/// a business state.
/// </summary>
public sealed class ScriptedChatClient : IChatClient
{
    public const string Reply = "Your taxi claim of 120 EUR is submitted. A finance reviewer will check it.";

    public TimeSpan Delay { get; init; } = TimeSpan.Zero;

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate u in GetStreamingResponseAsync(messages, options, cancellationToken)) updates.Add(u);
        return updates.ToChatResponse();
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        bool toolDone = messages.Any(m => m.Contents.OfType<FunctionResultContent>().Any());
        if (!toolDone && options?.Tools?.Count > 0)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call_1", "get_review_limit")])
            {
                MessageId = "msg_tool",
            };
            yield break;
        }
        foreach (string word in Reply.Split(' '))
        {
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
            else await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ") { MessageId = "msg_reply" };
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
