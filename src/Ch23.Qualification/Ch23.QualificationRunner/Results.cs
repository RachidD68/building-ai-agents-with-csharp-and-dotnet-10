using System.Text.RegularExpressions;

namespace Ch23.QualificationRunner;

public sealed record RowResult(string Id, string Promise, string Evidence, string? Test, string Data, string Expected,
    string Status, string? Observed, string? Artifact);

public sealed record ObservedModel(string Name, string? Digest, long? SizeBytes, string? ResponseModelId, string? Note);

public sealed record BudgetUse(double? BudgetMinutes, double SpentSeconds, double ModelLockSeconds);

public sealed record Totals(int Steps, int StepsPassed, int StepsCompleted, int StepsRefused, int StepsNotRun, int StepsFailed,
    int Cases, int CasesPassed, int Rows, int RowsPassed, int RowsFailed, int RowsNotRun, int RowsRefused, int RowsPendingHuman);

/// <summary>qualification-results.json: the one file the chapter and Appendix B read numbers from.</summary>
public sealed record QualificationResults
{
    public string Runner { get; init; } = "Ch23.QualificationRunner";
    public int Schema { get; init; } = 1;
    public required List<string> Modes { get; init; }
    public required string StartedUtc { get; init; }
    public required string FinishedUtc { get; init; }
    public required string Platform { get; init; }
    public required string SdkUsed { get; init; }
    public required string CandidateSha256 { get; init; }
    public required CandidateIdentity Candidate { get; init; }
    public required bool IdentityUnchangedAfterRun { get; init; }
    public ObservedModel? Model { get; init; }
    public required List<LayerResult> Layers { get; init; }
    public required List<RowResult> Matrix { get; init; }
    public required Totals Totals { get; init; }
    public required List<BudgetUse> Budgets { get; init; }
}

public static class Matrix
{
    public const string Passed = "passed", Failed = "failed", NotRun = "not-run", Refused = "refused", PendingHuman = "pending-human";

    /// <summary>
    /// A row is judged on the log of the step it names. A provider or workload promise
    /// is never satisfied by a step whose answers came from a fake or from no model.
    /// </summary>
    public static RowResult Evaluate(MatrixRow row, IReadOnlyDictionary<string, StepResult> steps, string outDir)
    {
        RowResult R(string status, string? observed, StepResult? s = null) =>
            new(row.Id, row.Promise, row.Evidence, row.Step is null ? null : $"{row.Step}{(row.Check is null ? "" : $": {row.Check}")}{(row.Ratio is null ? "" : $": /{row.Ratio}/ >= {row.Threshold:0.##}")}",
                row.Data, row.Expected, status, observed, s?.Log);

        if (row.Evidence == "human") return R(PendingHuman, "human acceptance is recorded apart; the runner never marks it");
        if (row.Step is null) return R(Refused, "no test: a promise without an observable failing test");
        if (!steps.TryGetValue(row.Step, out StepResult? step)) return R(NotRun, "the step is not in this run");
        if (row.Evidence is "provider" or "workload" && step.Provider is "fake" or "none")
            return R(Refused, $"{row.Evidence} promise, but {step.Id} ran with provider '{step.Provider}'", step);
        if (step.Status is StepStatus.NotRun or StepStatus.Stopped or StepStatus.Refused)
            return R(NotRun, $"step {step.Status}: {step.Reason}", step);
        string log = step.Log is null ? "" : File.ReadAllText(Path.Combine(outDir, step.Log));

        if (row.Check is not null)
        {
            string[] lines = log.Split('\n');
            if (lines.Any(l => Regex.IsMatch(l, @"^\s*PASS\s") && l.Contains(row.Check, StringComparison.Ordinal))) return R(Passed, "PASS line found", step);
            if (lines.Any(l => Regex.IsMatch(l, @"^\s*FAIL\s") && l.Contains(row.Check, StringComparison.Ordinal))) return R(Failed, "FAIL line found", step);
            return R(Refused, "the named check is not in the log", step);
        }
        if (row.Ratio is not null)
        {
            Match m = Regex.Match(log, row.Ratio, RegexOptions.Multiline);
            if (!m.Success) return R(Refused, "the named ratio is not in the log", step);
            int p = int.Parse(m.Groups["pass"].Value, System.Globalization.CultureInfo.InvariantCulture);
            int t = int.Parse(m.Groups["total"].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (t == 0) return R(Refused, "0 applicable items", step);
            return R((double)p / t >= row.Threshold ? Passed : Failed, $"{p}/{t}", step);
        }
        // The step's own counts: an offline step passes only at n/n; a provider step against the row's threshold.
        bool met = step.Status is StepStatus.Passed or StepStatus.Completed && step.Total > 0 && (double)step.Passed / step.Total >= row.Threshold;
        return R(met ? Passed : Failed, $"{step.Passed}/{step.Total}", step);
    }

    public static Totals Count(IReadOnlyList<LayerResult> layers, IReadOnlyList<RowResult> rows)
    {
        List<StepResult> s = [.. layers.SelectMany(l => l.Steps)];
        return new(s.Count, s.Count(x => x.Status == StepStatus.Passed), s.Count(x => x.Status == StepStatus.Completed),
            s.Count(x => x.Status == StepStatus.Refused), s.Count(x => x.Status is StepStatus.NotRun or StepStatus.Stopped),
            s.Count(x => x.Status == StepStatus.Failed), s.Sum(x => x.Total), s.Sum(x => x.Passed),
            rows.Count, rows.Count(r => r.Status == Passed), rows.Count(r => r.Status == Failed), rows.Count(r => r.Status == NotRun),
            rows.Count(r => r.Status == Refused), rows.Count(r => r.Status == PendingHuman));
    }
}
