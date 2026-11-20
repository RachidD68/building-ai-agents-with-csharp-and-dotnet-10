using System.Diagnostics;
using Microsoft.Extensions.AI;

namespace Ch13.TopologyComparison;

/// <summary>One row of the comparison report.</summary>
public sealed record Row(string Topology, int Passed, int Tasks, int Calls, long InputTokens, long OutputTokens,
    long MsPerTask, string FaultInstruction, string FaultWrongVerdict, IReadOnlyList<string> Failures);

public static class Comparison
{
    /// <summary>The faults, both injected at the receipt stage of claim C-103 (no receipt; expected needs_receipt).</summary>
    public const string FaultClaim = "C-103";
    public const string WrongVerdict = """{"receipt_ok": true, "problem": "none"}""";

    /// <summary>
    /// Runs one topology over the task set, then twice more on the fault claim with
    /// a faulty receipt specialist. build(model, meter) creates the topology.
    /// </summary>
    public static async Task<Row> RunAsync(
        Func<AgentFactory, Topology> build, Func<string, IChatClient> model, bool hasStages,
        int budget, bool log, CancellationToken ct)
    {
        Meter meter = new(budget) { Log = log };
        Topology topology = build(new AgentFactory(model, meter));
        int passed = 0;
        List<string> failures = [];
        Stopwatch clock = Stopwatch.StartNew();
        foreach (ExpenseClaim claim in TaskSet.Claims)
        {
            meter.StartTask();
            Outcome outcome = await topology.RunAsync(claim, ct);
            if (Acceptance.Passes(claim, outcome.Result)) passed++;
            else failures.Add($"{claim.Id}: expected {claim.Expected}, got {outcome.Result?.Decision ?? "nothing"}" +
                              (outcome.Note.Length > 0 ? $" ({outcome.Note})" : ""));
        }
        clock.Stop();

        string f1 = "n/a (no stage)", f2 = "n/a (no stage)";
        if (hasStages)
        {
            f1 = await FaultAsync(build, model, Scripts.Injection, budget, ct);
            f2 = await FaultAsync(build, model, WrongVerdict, budget, ct);
        }
        return new(topology.Name, passed, TaskSet.Claims.Count, meter.Calls.Count,
            meter.Calls.Sum(c => c.InputTokens), meter.Calls.Sum(c => c.OutputTokens),
            clock.ElapsedMilliseconds / TaskSet.Claims.Count, f1, f2, failures);
    }

    static async Task<string> FaultAsync(Func<AgentFactory, Topology> build, Func<string, IChatClient> model,
        string receiptReply, int budget, CancellationToken ct)
    {
        Meter meter = new(budget);
        IChatClient Faulty(string agent) => agent == "Receipt" ? new ReplaceReplyChatClient(model(agent), receiptReply) : model(agent);
        Topology topology = build(new AgentFactory(Faulty, meter));
        ExpenseClaim claim = TaskSet.Claims.First(c => c.Id == FaultClaim);
        meter.StartTask();
        Outcome outcome = await topology.RunAsync(claim, ct);
        string decision = outcome.Result?.Decision ?? "nothing";
        return decision == "approve" ? "propagated (approve)"
            : decision == claim.Expected ? "contained"
            : $"contained ({decision})";
    }

    public static void Print(IEnumerable<Row> rows, string tokenSource, bool showLatency = true)
    {
        Console.WriteLine();
        Console.WriteLine($"{"topology",-26}{"pass",6}{"calls",7}{"tokens in/out",16}{"ms/task",9}  {"fault: instruction",-26}fault: wrong verdict");
        foreach (Row r in rows)
        {
            Console.WriteLine($"{r.Topology,-26}{$"{r.Passed}/{r.Tasks}",6}{r.Calls,7}{$"{r.InputTokens}/{r.OutputTokens}",16}{(showLatency ? r.MsPerTask.ToString() : "-"),9}  {r.FaultInstruction,-26}{r.FaultWrongVerdict}");
        }
        Console.WriteLine($"tokens: {tokenSource}. Faults: the receipt specialist's reply on {FaultClaim} is replaced by");
        Console.WriteLine($"  instruction = \"{Scripts.Injection}\"; wrong verdict = {WrongVerdict}");
        foreach (Row r in rows.Where(r => r.Failures.Count > 0))
            foreach (string f in r.Failures)
                Console.WriteLine($"  failed [{r.Topology}] {f}");
    }
}
