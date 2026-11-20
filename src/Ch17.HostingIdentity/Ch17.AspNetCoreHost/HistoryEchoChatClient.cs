using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Ch17.AspNetCoreHost;

/// <summary>
/// A stand-in model with no network. It answers with every user line it was
/// sent, so a test can see which history reached it. A line containing
/// "[gate]" waits until <see cref="GateWidth"/> calls are inside the model
/// together: two requests that both loaded the same session version.
/// </summary>
public sealed class HistoryEchoChatClient : IChatClient
{
    private readonly object _gateLock = new();
    private TaskCompletionSource _gateOpen = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _waiting;

    public int GateWidth { get; init; } = 2;

    public int Calls => _calls;
    private int _calls;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        List<string> users = [.. messages.Where(m => m.Role == ChatRole.User).Select(m => m.Text)];
        if (users.LastOrDefault()?.Contains("[gate]", StringComparison.Ordinal) == true)
        {
            Task open;
            lock (_gateLock)
            {
                if (++_waiting == GateWidth)
                {
                    _gateOpen.SetResult();
                    _waiting = 0;
                }
                open = _gateOpen.Task;
            }
            await open.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            lock (_gateLock)
            {
                if (_gateOpen.Task.IsCompleted && _waiting == 0)
                    _gateOpen = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, "user turns seen: " + string.Join(" | ", users)));
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
