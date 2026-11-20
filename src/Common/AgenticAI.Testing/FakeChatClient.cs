using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgenticAI.Testing;

/// <summary>One scripted model reply: text chunks, then a finish reason, an error or a hang.</summary>
public sealed record FakeReply(
    IReadOnlyList<string> Chunks,
    ChatFinishReason? FinishReason,
    Exception? Error = null,
    bool HangAfterChunks = false,
    bool EndQuietlyOnCancel = false)
{
    /// <summary>A complete answer that ends with <c>Stop</c>, or with the reason given.</summary>
    public static FakeReply Text(string text, ChatFinishReason? finish = null) =>
        new([text], finish ?? ChatFinishReason.Stop);

    /// <summary>A streamed answer, one update per chunk.</summary>
    public static FakeReply Stream(ChatFinishReason finish, params string[] chunks) => new(chunks, finish);

    /// <summary>The chunks arrive, then the call throws, as a dropped connection would.</summary>
    public static FakeReply Throw(Exception error, params string[] chunksFirst) => new(chunksFirst, null, error);

    /// <summary>The chunks arrive, then the call waits until its token is cancelled.</summary>
    public static FakeReply Hang(params string[] chunksFirst) => new(chunksFirst, null, HangAfterChunks: true);

    /// <summary>
    /// The chunks arrive, the call waits for its token, and on cancellation it ends
    /// normally, with no exception and no finish reason: a provider that swallows
    /// the cancellation.
    /// </summary>
    public static FakeReply HangThenEndQuietly(params string[] chunksFirst) =>
        new(chunksFirst, null, HangAfterChunks: true, EndQuietlyOnCancel: true);
}

/// <summary>What the client was sent on one call.</summary>
public sealed record FakeCall(
    IReadOnlyList<ChatMessage> Messages, ChatOptions? Options, bool Streaming, CancellationToken Token)
{
    /// <summary>True when the call ended because its token was cancelled.</summary>
    public bool SawCancellation { get; internal set; }
}

/// <summary>
/// A deterministic <see cref="IChatClient"/>: it replays a script, records every
/// call and honours the cancellation token. No network, no model.
/// It proves wiring and contracts, never the quality of an answer.
/// </summary>
public sealed class FakeChatClient(params FakeReply[] script) : IChatClient
{
    private readonly Queue<FakeReply> _script = new(script);
    private readonly List<FakeCall> _calls = [];
    private readonly Lock _gate = new();

    /// <summary>Every call so far, in order.</summary>
    public IReadOnlyList<FakeCall> Calls { get { lock (_gate) return [.. _calls]; } }

    /// <summary>Adds replies at the end of the script.</summary>
    public FakeChatClient Then(params FakeReply[] replies)
    {
        lock (_gate) foreach (FakeReply r in replies) _script.Enqueue(r);
        return this;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        (FakeCall call, FakeReply reply) = Next(messages, options, streaming: false, cancellationToken);
        await Task.Yield();
        await FinishAsync(call, reply, cancellationToken);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Concat(reply.Chunks)))
        {
            FinishReason = reply.FinishReason,
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        (FakeCall call, FakeReply reply) = Next(messages, options, streaming: true, cancellationToken);
        foreach (string chunk in reply.Chunks)
        {
            await Task.Yield();
            Observe(call, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
        }
        if (await FinishAsync(call, reply, cancellationToken)) yield break;
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, FinishReason = reply.FinishReason };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    private (FakeCall, FakeReply) Next(
        IEnumerable<ChatMessage> messages, ChatOptions? options, bool streaming, CancellationToken token)
    {
        FakeCall call = new([.. messages], options?.Clone(), streaming, token);
        lock (_gate)
        {
            _calls.Add(call);
            if (!_script.TryDequeue(out FakeReply? reply))
                throw new InvalidOperationException($"FakeChatClient: no scripted reply left for call {_calls.Count}.");
            return (call, reply);
        }
    }

    /// <summary>Returns true when the call ended quietly on cancellation.</summary>
    private static async Task<bool> FinishAsync(FakeCall call, FakeReply reply, CancellationToken token)
    {
        if (reply.EndQuietlyOnCancel && token.IsCancellationRequested)
        {
            call.SawCancellation = true;
            return true;
        }
        Observe(call, token);
        if (reply.HangAfterChunks)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                call.SawCancellation = true;
                if (reply.EndQuietlyOnCancel) return true;
                throw;
            }
        }
        if (reply.Error is not null) throw reply.Error;
        return false;
    }

    private static void Observe(FakeCall call, CancellationToken token)
    {
        if (!token.IsCancellationRequested) return;
        call.SawCancellation = true;
        token.ThrowIfCancellationRequested();
    }
}
