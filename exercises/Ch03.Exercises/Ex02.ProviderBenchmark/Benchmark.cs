using System.Text;
using Microsoft.Extensions.AI;

namespace Ex02.ProviderBenchmark;

/// <summary>One call: time to the first text update, total time, or the error.</summary>
public sealed record CallTiming(TimeSpan? FirstToken, TimeSpan? Total, string? Error);

public static class Benchmark
{
    /// <summary>
    /// Sends <paramref name="prompt"/> <paramref name="calls"/> times, one after
    /// the other, and times each streamed answer on <paramref name="time"/>.
    /// </summary>
    public static async Task<IReadOnlyList<CallTiming>> RunAsync(IChatClient client, string prompt, int calls,
        TimeProvider time, CancellationToken ct = default)
    {
        List<CallTiming> runs = [];
        for (int i = 0; i < calls; i++)
        {
            long start = time.GetTimestamp();
            TimeSpan? first = null;
            try
            {
                await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(prompt, cancellationToken: ct))
                {
                    if (first is null && !string.IsNullOrEmpty(update.Text))
                    {
                        first = time.GetElapsedTime(start);
                    }
                }
                runs.Add(new CallTiming(first, time.GetElapsedTime(start), null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                runs.Add(new CallTiming(null, null, $"{ex.GetType().Name}: {ex.Message}"));
            }
        }
        return runs;
    }
}

/// <summary>Successes and failures kept apart; averages over successes only.</summary>
public sealed record BenchmarkSummary(int Succeeded, int Failed, double? AvgFirstTokenMs, double? AvgTotalMs)
{
    public static BenchmarkSummary From(IReadOnlyList<CallTiming> runs)
    {
        CallTiming[] ok = [.. runs.Where(r => r.Error is null)];
        double? Avg(Func<CallTiming, TimeSpan?> pick)
        {
            double[] ms = [.. ok.Select(pick).OfType<TimeSpan>().Select(t => t.TotalMilliseconds)];
            return ms.Length == 0 ? null : ms.Average();
        }
        return new BenchmarkSummary(ok.Length, runs.Count - ok.Length, Avg(r => r.FirstToken), Avg(r => r.Total));
    }

    public static string Table(IEnumerable<(string Label, BenchmarkSummary Summary)> rows)
    {
        StringBuilder sb = new();
        sb.AppendLine($"  {"Provider (model)",-28} {"OK",3} {"Failed",6} {"First token ms",15} {"Total ms",9}");
        foreach ((string label, BenchmarkSummary s) in rows)
        {
            sb.AppendLine($"  {label,-28} {s.Succeeded,3} {s.Failed,6} {Ms(s.AvgFirstTokenMs),15} {Ms(s.AvgTotalMs),9}");
        }
        return sb.ToString();
    }

    static string Ms(double? value) => value is { } v ? v.ToString("F0") : "-";
}
