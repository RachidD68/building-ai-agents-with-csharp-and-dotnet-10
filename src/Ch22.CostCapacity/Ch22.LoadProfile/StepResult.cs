using System.Globalization;

namespace Ch22.LoadProfile;

public sealed record StepResult(int Concurrency, IReadOnlyList<TaskTiming> Timings, TimeSpan Wall, int MaxQueueDepth)
{
    public int N => Timings.Count;
    public int Count(Outcome o) => Timings.Count(t => t.Outcome == o);
    public int Errors => Count(Outcome.Throttled) + Count(Outcome.Timeout) + Count(Outcome.Error);
    public double Rate(int count) => N == 0 ? 0 : (double)count / N;

    /// <summary>Latency clocks over the requests that got an answer (accepted or rejected).</summary>
    public IEnumerable<TaskTiming> Answered => Timings.Where(t => t.Outcome is Outcome.Accepted or Outcome.Rejected);

    public double? P(double q, Func<TaskTiming, TimeSpan?> clock) =>
        Percentile.NearestRank([.. Answered.Select(clock).OfType<TimeSpan>().Select(t => t.TotalMilliseconds)], q);

    public double AcceptedPerSecond => Wall.TotalSeconds == 0 ? 0 : Count(Outcome.Accepted) / Wall.TotalSeconds;

    /// <summary>The first line of the declared limit this step breaks, or null.</summary>
    public string? Crosses(DeclaredLimit limit)
    {
        if (P(0.95, t => t.Total) is { } p95 && p95 > limit.P95TotalMs) return $"p95 total {p95:F0} ms > {limit.P95TotalMs:F0} ms";
        if (Rate(Errors) > limit.MaxErrorRate) return $"error rate {Percentile.Pct(Rate(Errors))} > {Percentile.Pct(limit.MaxErrorRate)}";
        if (Rate(Count(Outcome.Busy)) > limit.MaxBusyRate) return $"busy rate {Percentile.Pct(Rate(Count(Outcome.Busy)))} > {Percentile.Pct(limit.MaxBusyRate)}";
        if (Rate(Count(Outcome.Accepted)) < limit.MinAcceptedRate)
            return $"accepted rate {Percentile.Pct(Rate(Count(Outcome.Accepted)))} < {Percentile.Pct(limit.MinAcceptedRate)}";
        return null;
    }
}

public sealed record LoadReport(string WorkloadVersion, IReadOnlyList<StepResult> Steps)
{
    /// <summary>The highest step that held, if the run reached one that did not.</summary>
    public string Declare(DeclaredLimit limit)
    {
        StepResult? crossed = Steps.FirstOrDefault(s => s.Crosses(limit) is not null);
        StepResult? held = Steps.TakeWhile(s => s.Crosses(limit) is null).LastOrDefault();
        return (held, crossed) switch
        {
            (null, { } c) => $"no step held: concurrency {c.Concurrency} already crosses ({c.Crosses(limit)})",
            ({ } h, { } c) => $"declared limit: concurrency {h.Concurrency} (n={h.N}); concurrency {c.Concurrency} crosses: {c.Crosses(limit)}",
            ({ } h, null) => $"not reached: every step held, up to concurrency {h.Concurrency} (n={h.N}); step higher to find the limit",
            _ => "no steps ran",
        };
    }
}

public static class Percentile
{
    /// <summary>Nearest-rank percentile; null for an empty sample, never a made-up zero.</summary>
    public static double? NearestRank(double[] values, double q)
    {
        if (values.Length == 0) return null;
        Array.Sort(values);
        int rank = (int)Math.Ceiling(q * values.Length);
        return values[Math.Clamp(rank, 1, values.Length) - 1];
    }

    public static string Pct(double rate) => $"{(rate * 100).ToString("F0", CultureInfo.InvariantCulture)}%";

    public static string Ms(double? value) => value is { } v ? v.ToString("F0", CultureInfo.InvariantCulture) : "-";
}
