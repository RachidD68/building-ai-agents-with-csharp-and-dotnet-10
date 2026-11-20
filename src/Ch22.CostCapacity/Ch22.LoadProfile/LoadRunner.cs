using System.Net;
using System.Text;
using Microsoft.Extensions.AI;

namespace Ch22.LoadProfile;

public enum Outcome { Accepted, Rejected, Busy, Throttled, Timeout, Error }

/// <summary>
/// One request's clocks, each a difference of monotonic timestamps:
/// queue (arrival to slot), tool wait, time to first token (from the model
/// call), generation (first token to last update) and total (arrival to done).
/// Human wait is not in a load test; it belongs to the task, not the service.
/// </summary>
public sealed record TaskTiming(string TaskId, Outcome Outcome, TimeSpan Queue, TimeSpan Tool,
    TimeSpan? Ttft, TimeSpan? Generation, TimeSpan Total, UsageDetails? Usage, string? Error = null);

public sealed class LoadRunner(IChatClient model, Workload workload, TimeProvider clock,
    Func<CancellationToken, Task> tool)
{
    readonly ChatOptions _options = new() { MaxOutputTokens = workload.MaxOutputTokens, Temperature = 0 };

    public AdmissionGate Gate { get; private set; } =
        new(workload.Service.Slots, workload.Service.QueueCapacity);

    public async Task<TaskTiming> RunOneAsync(WorkTask task, CancellationToken ct)
    {
        long arrived = clock.GetTimestamp();
        using AdmissionGate.Lease? lease = await Gate.TryEnterAsync(ct);
        if (lease is null) return new(task.Id, Outcome.Busy, default, default, null, null, clock.GetElapsedTime(arrived), null);

        long admitted = clock.GetTimestamp(), toolDone = admitted;
        long? first = null;
        UsageDetails? usage = null;
        using CancellationTokenSource deadline = new(TimeSpan.FromMilliseconds(workload.TimeoutMs), clock);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        TaskTiming Done(Outcome outcome, string? error = null)
        {
            long end = clock.GetTimestamp();
            if (outcome is Outcome.Accepted or Outcome.Rejected && deadline.IsCancellationRequested && !ct.IsCancellationRequested)
                outcome = Outcome.Timeout;   // an answer that arrived after the deadline is late, whatever it says
            return new(task.Id, outcome, clock.GetElapsedTime(arrived, admitted), clock.GetElapsedTime(admitted, toolDone),
                first is { } f ? clock.GetElapsedTime(toolDone, f) : null,
                first is { } g ? clock.GetElapsedTime(g, end) : null, clock.GetElapsedTime(arrived, end), usage, error);
        }
        try
        {
            await tool(linked.Token);
            toolDone = clock.GetTimestamp();
            StringBuilder text = new();
            List<ChatMessage> messages = [new(ChatRole.System, workload.System), new(ChatRole.User, task.Prompt)];
            await foreach (ChatResponseUpdate update in model.GetStreamingResponseAsync(messages, _options, linked.Token))
            {
                if (first is null && !string.IsNullOrEmpty(update.Text)) first = clock.GetTimestamp();
                text.Append(update.Text);
                foreach (UsageContent u in update.Contents.OfType<UsageContent>()) (usage ??= new()).Add(u.Details);
            }
            bool accepted = text.ToString().Contains(task.Expected, StringComparison.OrdinalIgnoreCase);
            return Done(accepted ? Outcome.Accepted : Outcome.Rejected);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return Done(Outcome.Timeout);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return Done(Outcome.Throttled);   // no retry here: the load runner reports, it owns no retry
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Done(Outcome.Error, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// One step: <paramref name="concurrency"/> clients send RequestsPerStep
    /// requests between them, back to back. A busy client waits
    /// BusyRetryAfterMs before its next request.
    /// </summary>
    public async Task<StepResult> RunStepAsync(int concurrency, CancellationToken ct)
    {
        Gate = new(workload.Service.Slots, workload.Service.QueueCapacity);
        int next = -1;
        List<TaskTiming> timings = [];
        long start = clock.GetTimestamp();
        Task[] clients = [.. Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
        {
            int i;
            while ((i = Interlocked.Increment(ref next)) < workload.RequestsPerStep)
            {
                TaskTiming t = await RunOneAsync(workload.Tasks[i % workload.Tasks.Count], ct);
                lock (timings) timings.Add(t);
                if (t.Outcome == Outcome.Busy)
                    await Task.Delay(TimeSpan.FromMilliseconds(workload.Service.BusyRetryAfterMs), clock, ct);
            }
        }, ct))];
        await Task.WhenAll(clients);
        return new StepResult(concurrency, timings, clock.GetElapsedTime(start), Gate.MaxQueueDepth);
    }

    /// <summary>Steps up until a step crosses the declared limit, then stops.</summary>
    public async Task<LoadReport> RunAsync(Action<StepResult>? onStep, CancellationToken ct)
    {
        List<StepResult> steps = [];
        foreach (int c in workload.Steps)
        {
            StepResult step = await RunStepAsync(c, ct);
            steps.Add(step);
            onStep?.Invoke(step);
            if (step.Crosses(workload.Limit) is not null) break;
        }
        return new LoadReport(workload.Version, steps);
    }
}
