using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Ch14.ApprovalWorkflow;

/// <summary>
/// A fake IChatClient for --offline, --self-test and Ch14.WorkflowTests. It
/// answers every call with a fixed summary plus a call counter, so a test can
/// see which revision it is looking at, and it never contacts a model.
/// </summary>
public sealed class ScriptedChatClient(string reply = "Scripted summary.") : IChatClient
{
    private int _calls;

    public int Calls => _calls;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        int call = Interlocked.Increment(ref _calls);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"{reply} (draft {call})")));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
