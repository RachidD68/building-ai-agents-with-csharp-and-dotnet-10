using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ch21.EvidenceEval;

/// <summary>
/// What the release decision will use, written down before any candidate runs.
/// The hash of this record goes into the run log; a decision made against a plan
/// whose hash changed after the run started is refused.
/// </summary>
public sealed record GatePlan(
    string SetId, int Items, int Repetitions, double MinItemPassRate, IReadOnlyList<string> GuardedSlices)
{
    public string Hash() => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this))))[..12];
}

/// <summary>One held-out item for one candidate: how many of its repetitions passed every layer.</summary>
public sealed record ItemOutcome(string CaseId, string Slice, int RepsPassed, int Reps)
{
    /// <summary>The item counts as passed only when every repetition passed.</summary>
    public bool Passed => RepsPassed == Reps;
}

public sealed record Decision(
    string Verdict, IReadOnlyList<string> Reasons, int N, int Passed, double Low, double High,
    int Wins, int Losses, IReadOnlyList<string> GuardedLosses);

public static class ReleaseGate
{
    /// <summary>
    /// Paired comparison on the same held-out items. The unit is the item, not the
    /// repetition: repetitions of one item are correlated, so they do not add trials.
    /// </summary>
    public static Decision Decide(GatePlan plan, string declaredHash,
        IReadOnlyList<ItemOutcome> baseline, IReadOnlyList<ItemOutcome> candidate)
    {
        if (plan.Hash() != declaredHash)
            return new("REFUSED", [$"plan hash {plan.Hash()} is not the declared {declaredHash}: thresholds moved after the run"],
                0, 0, 0, 0, 0, 0, []);
        Dictionary<string, ItemOutcome> before = baseline.ToDictionary(o => o.CaseId);
        if (candidate.Count != plan.Items || candidate.Any(o => !before.ContainsKey(o.CaseId) || o.Reps != plan.Repetitions))
            return new("REFUSED", ["the candidate did not run the declared items and repetitions"], 0, 0, 0, 0, 0, 0, []);

        int n = candidate.Count, passed = candidate.Count(o => o.Passed);
        (double low, double high) = Wilson(passed, n);
        int wins = candidate.Count(o => o.Passed && !before[o.CaseId].Passed);
        int losses = candidate.Count(o => !o.Passed && before[o.CaseId].Passed);
        string[] guardedLosses = [.. candidate.Where(o => !o.Passed && before[o.CaseId].Passed && plan.GuardedSlices.Contains(o.Slice))
            .Select(o => $"{o.CaseId} ({o.Slice})")];

        List<string> reasons = [];
        if (guardedLosses.Length > 0) reasons.Add($"regressed in a guarded slice: {string.Join(", ", guardedLosses)}");
        if (low < plan.MinItemPassRate) reasons.Add($"lower bound {low:F2} is below the declared {plan.MinItemPassRate:F2}");
        return new(reasons.Count == 0 ? "SHIP" : "BLOCK", reasons, n, passed, low, high, wins, losses, guardedLosses);
    }

    /// <summary>The gate a single headline number gives: ship when the mean repetition score went up.</summary>
    public static bool OverallOnly(IReadOnlyList<ItemOutcome> baseline, IReadOnlyList<ItemOutcome> candidate) =>
        candidate.Average(o => (double)o.RepsPassed / o.Reps) > baseline.Average(o => (double)o.RepsPassed / o.Reps);

    /// <summary>Wilson score interval, 95 % (z = 1.96), for k successes in n independent trials.</summary>
    public static (double Low, double High) Wilson(int k, int n, double z = 1.96)
    {
        if (n == 0) return (0, 1);
        double p = (double)k / n, z2 = z * z, denom = 1 + z2 / n;
        double centre = (p + z2 / (2 * n)) / denom;
        double half = z * Math.Sqrt(p * (1 - p) / n + z2 / (4.0 * n * n)) / denom;
        return (Math.Max(0, centre - half), Math.Min(1, centre + half));
    }

    /// <summary>
    /// Rule of three: with zero failures in n independent, representative trials,
    /// the 95 % upper bound on the failure rate is about 3/n.
    /// </summary>
    public static double RuleOfThree(int independentTrials) => 3.0 / independentTrials;
}
