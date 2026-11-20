using Microsoft.Agents.AI;

namespace Ch16.HarnessLoop;

public enum LoopOutcome { Stopped, CapReached }

/// <summary>
/// Wraps a LoopEvaluator and remembers its last verdict. LoopAgent returns the last
/// response whether an evaluator said Stop or the cap ran out (and in 1.28 it does not
/// evaluate the iteration that hits the cap), so leaving the loop says nothing about
/// success. This wrapper is how the caller tells the two apart. Its state is one run's:
/// build new recorders for every RunAsync, never share them between runs or sessions.
/// </summary>
public sealed class RecordingLoopEvaluator(LoopEvaluator inner) : LoopEvaluator
{
    public int Evaluations { get; private set; }
    public int LastIteration { get; private set; }
    public bool LastSaidStop { get; private set; }

    public override async ValueTask<LoopEvaluation> EvaluateAsync(LoopContext context, CancellationToken cancellationToken = default)
    {
        LoopEvaluation verdict = await inner.EvaluateAsync(context, cancellationToken);
        Evaluations++;
        LastIteration = context.Iteration;
        LastSaidStop = !verdict.ShouldReinvoke;
        return verdict;
    }

    /// <summary>Stopped only when every evaluator's last word was Stop.</summary>
    public static LoopOutcome Outcome(IEnumerable<RecordingLoopEvaluator> evaluators) =>
        evaluators.All(e => e.Evaluations > 0 && e.LastSaidStop) ? LoopOutcome.Stopped : LoopOutcome.CapReached;

    /// <summary>Process exit code: 0 only when the loop stopped on its evaluators.</summary>
    public static int ExitCode(LoopOutcome outcome) => outcome == LoopOutcome.Stopped ? 0 : 2;
}

/// <summary>The rule the demo's evaluator enforces: the price is stated and the summary fits a word budget.</summary>
public static class SummaryRule
{
    public const int WordBudget = 22;

    public static IReadOnlyList<string> Problems(string? reply)
    {
        string text = (reply ?? "").Replace("DONE", "").Trim();
        int words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        List<string> problems = [];
        if (words > WordBudget) problems.Add($"it is {words} words, the limit is {WordBudget}");
        if (!text.Contains('$')) problems.Add("it does not state the price");
        return problems;
    }
}
