using Ch22.CostModel;
using Ch22.LoadProfile;
using Microsoft.Extensions.AI;

namespace Ch22.ModelRouting;

public sealed record RoutedTask(string Id, string Prompt, string Expected, bool Hard);

/// <summary>One configuration's result on the shared task set: the same report for every candidate.</summary>
public sealed record CandidateReport(string Name, int N, int Accepted, int Attempts, int FailedAttempts,
    CostReport Cost, double? P95Ms, int? Misroutes)
{
    public string Row() =>
        $"{Name,-36} {Accepted,3}/{N,-3} {Attempts,8} {FailedAttempts,6} {CostReport.Money(Cost.CostPerAcceptedTask, Cost.Currency),18}" +
        $"{(Cost.Complete ? " " : "*")} {Percentile.Ms(P95Ms),8} {(Misroutes is { } m ? m.ToString() : "-"),9}";

    public static string Header =>
        $"{"Candidate",-36} {"acc",7} {"attempts",8} {"failed",6} {"per accepted task",18}  {"p95 ms",8} {"misroutes",9}";

    /// <summary>Adopt only what beats the baseline on the same tasks: no fewer accepted,
    /// a lower cost per accepted task, and a p95 no more than 10% higher.</summary>
    public string VerdictAgainst(CandidateReport baseline) =>
        Accepted < baseline.Accepted ? $"reject: {baseline.Accepted - Accepted} fewer accepted"
        : Cost.CostPerAcceptedTask is not { } c || baseline.Cost.CostPerAcceptedTask is not { } b ? "reject: cost undefined"
        : c >= b ? "reject: no cheaper per accepted task"
        : P95Ms > baseline.P95Ms * 1.10 ? "reject: p95 more than 10% higher"
        : "adopt";
}

public static class Comparison
{
    static readonly ChatOptions s_options = new() { MaxOutputTokens = 4, Temperature = 0 };

    /// <summary>
    /// Runs every task once, in order, through <paramref name="candidate"/>.
    /// <paramref name="attempts"/> is the log the metered tiers write to.
    /// </summary>
    public static async Task<CandidateReport> RunAsync(string name, IChatClient candidate, List<Attempt> attempts,
        IReadOnlyList<RoutedTask> tasks, string system, PriceTable prices, TimeProvider clock,
        DifficultyRouterChatClient? router = null, string runtimeHost = "", CancellationToken ct = default)
    {
        attempts.Clear();
        router?.Decisions.Clear();
        List<TaskRecord> outcomes = [];
        List<double> totals = [];
        long runStart = clock.GetTimestamp();
        foreach (RoutedTask task in tasks)
        {
            long start = clock.GetTimestamp();
            string outcome;
            try
            {
                ChatResponse r = await candidate.GetResponseAsync(
                    [new(ChatRole.System, system), new(ChatRole.User, task.Prompt)], s_options, ct);
                outcome = r.Text.Contains(task.Expected, StringComparison.OrdinalIgnoreCase) ? "accepted" : "rejected";
                totals.Add(clock.GetElapsedTime(start).TotalMilliseconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outcome = "error";
            }
            outcomes.Add(new(task.Id, outcome));
        }

        Dictionary<string, decimal>? runtime = runtimeHost.Length == 0 ? null
            : new() { [runtimeHost] = (decimal)clock.GetElapsedTime(runStart).TotalHours };
        UsageLog log = new(name, DateOnly.FromDateTime(DateTime.Now),
            [.. attempts.Select((a, i) => new CallRecord($"a{i}", a.Model, 1, a.Usage, Status: a.Error ?? "ok"))],
            outcomes, runtime);
        int? misroutes = router is null ? null
            : router.Decisions.Count(d => d.RoutedHard != tasks.First(t => t.Prompt == d.Prompt).Hard);
        return new(name, tasks.Count, outcomes.Count(o => o.Accepted), attempts.Count, attempts.Count(a => a.Error is not null),
            CostCalculator.Compute(prices, log), Percentile.NearestRank([.. totals], 0.95), misroutes);
    }
}
