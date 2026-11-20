using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Ch13.TopologyComparison;

/// <summary>One model call, as the meter saw it.</summary>
public sealed record CallRecord(string Agent, long InputTokens, long OutputTokens, long Ms);

/// <summary>Thrown when one task spends more model calls than its budget.</summary>
public sealed class CallBudgetExceededException(int budget)
    : Exception($"call budget of {budget} model calls exceeded");

/// <summary>
/// Counts model calls and tokens per agent, and stops a task that spends more
/// calls than its budget. One meter per topology run; Reset() between tasks.
/// </summary>
public sealed class Meter(int callBudgetPerTask)
{
    readonly ConcurrentQueue<CallRecord> _calls = new();
    int _callsThisTask;

    public IReadOnlyCollection<CallRecord> Calls => _calls;
    public bool Log { get; init; }

    public void StartTask() => Interlocked.Exchange(ref _callsThisTask, 0);

    internal void Enter()
    {
        if (Interlocked.Increment(ref _callsThisTask) > callBudgetPerTask)
            throw new CallBudgetExceededException(callBudgetPerTask);
    }

    internal void Record(CallRecord call)
    {
        _calls.Enqueue(call);
        if (Log)
            Console.WriteLine($"    [call] {call.Agent,-12} in={call.InputTokens,5} out={call.OutputTokens,4} {call.Ms,6} ms");
    }

    public IChatClient Wrap(IChatClient inner, string agent) => new CountingChatClient(inner, agent, this);
}

/// <summary>Sits under one agent; every model round trip (tool rounds included) is one call.</summary>
public sealed class CountingChatClient(IChatClient inner, string agent, Meter meter) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        meter.Enter();
        long start = Environment.TickCount64;
        ChatResponse response = await base.GetResponseAsync(messages, options, cancellationToken);
        meter.Record(new(agent, response.Usage?.InputTokenCount ?? 0, response.Usage?.OutputTokenCount ?? 0,
            Environment.TickCount64 - start));
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        meter.Enter();
        long start = Environment.TickCount64, input = 0, output = 0;
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            foreach (UsageContent usage in update.Contents.OfType<UsageContent>())
            {
                input += usage.Details.InputTokenCount ?? 0;
                output += usage.Details.OutputTokenCount ?? 0;
            }
            yield return update;
        }
        meter.Record(new(agent, input, output, Environment.TickCount64 - start));
    }
}
