using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ch04.FirstAgent;

/// <summary>How an agent run ended. Exactly one of these, never "some text arrived".</summary>
public enum RunStatus { Completed, Cancelled, Truncated, Failed }

/// <summary>
/// The terminal state of one run. <see cref="Text"/> holds whatever arrived,
/// complete or partial; only <see cref="RunStatus.Completed"/> is a success.
/// </summary>
public sealed record RunOutcome(RunStatus Status, string Text, ChatFinishReason? FinishReason, Exception? Error)
{
    public bool IsSuccess => Status == RunStatus.Completed;
}

/// <summary>
/// Follows a streamed run one update at a time and records how it ended.
/// The caller's token decides what "cancelled" means: an
/// <see cref="OperationCanceledException"/> the caller did not ask for (a time
/// budget, an HTTP timeout) is a failure, not a cancellation. A stream can
/// also end quietly, with no exception and no finish reason, after its token
/// was cancelled (seen live with Ollama): the tokens decide that case too, so
/// it is never reported as Completed. Pass the run's own token as
/// <paramref name="runToken"/> when it differs from the caller's (a time budget).
/// </summary>
public sealed class RunTracker(CancellationToken cancelledByCaller, CancellationToken runToken = default)
{
    private readonly StringBuilder _text = new();
    private ChatFinishReason? _finish;
    private RunStatus? _status;
    private Exception? _error;

    /// <summary>The next update, or null once the run has ended (see <see cref="Outcome"/>).</summary>
    public async ValueTask<AgentResponseUpdate?> NextAsync(IAsyncEnumerator<AgentResponseUpdate> updates)
    {
        if (_status is not null) return null;
        try
        {
            if (!await updates.MoveNextAsync())
            {
                if (cancelledByCaller.IsCancellationRequested) _status = RunStatus.Cancelled;
                else if (runToken.IsCancellationRequested)
                    (_status, _error) = (RunStatus.Failed, new OperationCanceledException(runToken));
                else _status = AgentRuns.Classify(_finish);
                return null;
            }
        }
        catch (OperationCanceledException) when (cancelledByCaller.IsCancellationRequested)
        {
            _status = RunStatus.Cancelled;
            return null;
        }
        catch (Exception error)          // recorded, not swallowed: Outcome.Error keeps it as thrown
        {
            _status = RunStatus.Failed;
            _error = error;
            return null;
        }
        AgentResponseUpdate update = updates.Current;
        _text.Append(update.Text);
        _finish = update.FinishReason ?? _finish;
        return update;
    }

    /// <summary>True once <see cref="NextAsync"/> has returned null.</summary>
    public bool HasEnded => _status is not null;

    /// <summary>The terminal state; throws if the run has not ended yet.</summary>
    public RunOutcome Outcome => _status is { } status
        ? new RunOutcome(status, _text.ToString(), _finish, _error)
        : throw new InvalidOperationException("The run has not ended.");
}

public static class AgentRuns
{
    /// <summary>A finish reason mapped to a terminal state. Length means the answer was cut off.</summary>
    public static RunStatus Classify(ChatFinishReason? finish) =>
        finish == ChatFinishReason.Length ? RunStatus.Truncated
        : finish == ChatFinishReason.ContentFilter ? RunStatus.Failed
        : RunStatus.Completed;

    /// <summary>Reads a whole stream, passing each piece of text on as it arrives.</summary>
    public static async Task<RunOutcome> CollectAsync(
        IAsyncEnumerable<AgentResponseUpdate> updates, CancellationToken cancelledByCaller,
        Action<string>? onText = null, CancellationToken runToken = default)
    {
        RunTracker tracker = new(cancelledByCaller, runToken);
        await using IAsyncEnumerator<AgentResponseUpdate> e = updates.GetAsyncEnumerator();
        while (await tracker.NextAsync(e) is { } update)
        {
            if (update.Text is { Length: > 0 } text) onText?.Invoke(text);
        }
        return tracker.Outcome;
    }

    /// <summary>The same terminal states for a non-streamed run.</summary>
    public static async Task<RunOutcome> RunAsync(
        Func<Task<AgentResponse>> run, CancellationToken cancelledByCaller, CancellationToken runToken = default)
    {
        try
        {
            AgentResponse response = await run();
            // A provider that returns quietly after a cancellation: the tokens decide, as for a stream.
            if (cancelledByCaller.IsCancellationRequested)
                return new RunOutcome(RunStatus.Cancelled, response.Text, response.FinishReason, null);
            if (runToken.IsCancellationRequested)
                return new RunOutcome(RunStatus.Failed, response.Text, response.FinishReason, new OperationCanceledException(runToken));
            return new RunOutcome(Classify(response.FinishReason), response.Text, response.FinishReason, null);
        }
        catch (OperationCanceledException) when (cancelledByCaller.IsCancellationRequested)
        {
            return new RunOutcome(RunStatus.Cancelled, "", null, null);
        }
        catch (Exception error)          // recorded, not swallowed: the outcome keeps it as thrown
        {
            return new RunOutcome(RunStatus.Failed, "", null, error);
        }
    }
}
