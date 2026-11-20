using System.Diagnostics;

namespace Ch22.LoadProfile;

/// <summary>Offline: dotnet run -- --self-test. No model, no network.</summary>
internal static class LoadProfileSelfTest
{
    static int s_passed, s_total;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Load profile: self-test ===");
        Workload offline = Workload.Load(Path.Combine(AppContext.BaseDirectory, "data", "workload.offline.v1.json"));

        await ClocksAsync(offline);
        await AdmissionAsync();
        await StepsAsync(offline);
        await FailuresAsync(offline);

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    // One slot, a queue of one. A runs; B waits in the queue; C finds it full.
    static async Task ClocksAsync(Workload offline)
    {
        Console.WriteLine("\n-- Clocks kept apart (manual clock, the test releases each step) --");
        ManualClock clock = new();
        GatedModel.Call a = new(), b = new();
        Queue<(TaskCompletionSource Started, TaskCompletionSource Release)> tools = new([(New(), New()), (New(), New())]);
        Task Tool(CancellationToken _)
        {
            (TaskCompletionSource started, TaskCompletionSource release) = tools.Dequeue();
            started.SetResult();
            return release.Task;
        }
        (TaskCompletionSource Started, TaskCompletionSource Release)[] t = [.. tools];
        Workload w = offline with { Service = new(Slots: 1, QueueCapacity: 1, BusyRetryAfterMs: 0), TimeoutMs = 600_000 };
        LoadRunner runner = new(new GatedModel(a, b), w, clock, Tool);
        WorkTask task = w.Tasks[0] with { Expected = "APPROVE" };

        Task<TaskTiming> runA = runner.RunOneAsync(task, default);
        await t[0].Started.Task; clock.Advance(200); t[0].Release.SetResult();     // tool: 200 ms
        await a.Started.Task; clock.Advance(150); a.First.SetResult();             // first token: 150 ms
        await a.FirstSeen.Task;
        Task<TaskTiming> runB = runner.RunOneAsync(task, default);                  // B arrives at 350, queued
        TaskTiming c = await runner.RunOneAsync(task, default);                     // C: queue full
        clock.Advance(250); a.End.SetResult();                                      // generation: 250 ms
        TaskTiming ta = await runA;
        await t[1].Started.Task; clock.Advance(100); t[1].Release.SetResult();     // B admitted at 600
        await b.Started.Task; clock.Advance(50); b.First.SetResult();
        await b.FirstSeen.Task; clock.Advance(250); b.End.SetResult();
        TaskTiming tb = await runB;

        Console.WriteLine($"    A: {Clocks(ta)}");
        Console.WriteLine($"    B: {Clocks(tb)}");
        Console.WriteLine($"    C: {c.Outcome}, total {c.Total.TotalMilliseconds:F0}");
        Expect("A: queue 0, tool 200, ttft 150, generation 250, total 600", Is(ta, 0, 200, 150, 250, 600));
        Expect("B: its 250 ms in the queue is reported apart from its 250 ms of generation", Is(tb, 250, 100, 50, 250, 650));
        Expect("C: the queue is full, so a busy response at once, not a wait", c is { Outcome: Outcome.Busy } && c.Total == TimeSpan.Zero);
        Expect("the queue never held more than its capacity (1)", runner.Gate.MaxQueueDepth == 1);
        Expect("usage is read from the stream's UsageContent, not estimated", ta.Usage?.InputTokenCount == 40);
    }

    // 32 callers on the thread pool against 2 slots and a queue of 4.
    static async Task AdmissionAsync()
    {
        Console.WriteLine("\n-- Admission control under 32 concurrent callers (2 slots, queue 4) --");
        AdmissionGate gate = new(slots: 2, queueCapacity: 4);
        TaskCompletionSource release = New();
        int busy = 0, served = 0;
        Task[] callers = [.. Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
        {
            Task<AdmissionGate.Lease?> entry = gate.TryEnterAsync(default);
            if (entry.IsCompleted && entry.Result is null) { Interlocked.Increment(ref busy); return; }
            using AdmissionGate.Lease? lease = await entry;
            await release.Task;
            Interlocked.Increment(ref served);
        }))];
        while (Volatile.Read(ref busy) < 26 && !callers.All(c => c.IsCompleted)) await Task.Delay(10);
        int busyWhileFull = Volatile.Read(ref busy);
        release.SetResult();
        await Task.WhenAll(callers);
        Console.WriteLine($"    busy {busy}, served {served}, max queue depth {gate.MaxQueueDepth}");
        Expect("2 run, 4 wait, the other 26 get a busy response while both are full", busyWhileFull == 26 && served == 6);
        Expect("the queue depth never passed its capacity", gate.MaxQueueDepth == 4);
    }

    static async Task StepsAsync(Workload offline)
    {
        Console.WriteLine("\n-- Stepped load (scripted model: 429 beyond 3 in flight; 4 service slots) --");
        ScriptedModel model = new(TimeProvider.System, TimeSpan.FromMilliseconds(15), TimeSpan.FromMilliseconds(35), capacity: 3);
        LoadRunner runner = new(model, offline, TimeProvider.System,
            ct => Task.Delay(TimeSpan.FromMilliseconds(offline.ToolDelayMs), ct));
        LoadReport report = await runner.RunAsync(s => Console.WriteLine($"    {RunRecord.Row(s)}"), default);
        Console.WriteLine($"    {report.Declare(offline.Limit)}");
        StepResult last = report.Steps[^1];
        Expect("concurrency 1 and 2 never exceed the provider's 3: no 429, both steps hold",
            report.Steps.Take(2).All(s => s.Errors == 0 && s.Crosses(offline.Limit) is null));
        Expect("the run stops at the first step that crosses the limit",
            last.Crosses(offline.Limit) is not null && report.Steps.SkipLast(1).All(s => s.Crosses(offline.Limit) is null));
        Expect("every scripted 429 is counted as a throttled error, none as accepted",
            model.Throttled == report.Steps.Sum(s => s.Count(Outcome.Throttled)) && model.Throttled > 0);
        Expect("the declared limit names the last step that held, with n",
            report.Declare(offline.Limit).StartsWith($"declared limit: concurrency {report.Steps[^2].Concurrency} (n={offline.RequestsPerStep})"));
    }

    static async Task FailuresAsync(Workload offline)
    {
        Console.WriteLine("\n-- Where it fails --");
        double[] totals = [5_000, .. Enumerable.Repeat(500.0, 19)];
        Console.WriteLine($"    20 requests, the first one cold: mean {totals.Average():F0} ms, " +
                          $"p95 {Percentile.NearestRank([.. totals], 0.95):F0} ms, max {totals.Max():F0} ms");
        Expect("DEFECT REPRODUCED  a 5,000 ms cold start hides in the mean (725) and below p95's rank (500)",
            totals.Average() < 1_000 && Percentile.NearestRank([.. totals], 0.95) == 500);

        Workload fast = offline with { TimeoutMs = 50, ToolDelayMs = 0 };
        ScriptedModel deaf = new(TimeProvider.System, TimeSpan.FromMilliseconds(300), TimeSpan.Zero, ignoreCancellation: true);
        Stopwatch sw = Stopwatch.StartNew();
        TaskTiming late = await new LoadRunner(deaf, fast, TimeProvider.System, _ => Task.CompletedTask).RunOneAsync(fast.Tasks[0], default);
        Console.WriteLine($"    connector that ignores the token: {late.Outcome} after {sw.ElapsedMilliseconds} ms (timeout 50 ms)");
        Expect("DEFECT REPRODUCED  a connector that ignores cancellation holds the slot past the 50 ms timeout, and the work is done",
            sw.ElapsedMilliseconds >= 250 && deaf.Completed == 1);

        ScriptedModel honest = new(TimeProvider.System, TimeSpan.FromMilliseconds(300), TimeSpan.Zero);
        sw.Restart();
        TaskTiming cut = await new LoadRunner(honest, fast, TimeProvider.System, _ => Task.CompletedTask).RunOneAsync(fast.Tasks[0], default);
        Console.WriteLine($"    connector that honours it: {cut.Outcome} after {sw.ElapsedMilliseconds} ms");
        Expect("a connector that honours the token: Timeout, slot freed, no completed work", cut.Outcome == Outcome.Timeout && honest.Completed == 0);

        Expect("an empty sample has no percentile (null, not 0)", Percentile.NearestRank([], 0.95) is null);
        string unversioned = Path.Combine(Path.GetTempPath(), $"workload-{Guid.NewGuid():N}.json");
        File.WriteAllText(unversioned, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "workload.offline.v1.json"))
            .Replace("\"version\": \"contoso-claims-triage/1-offline\"", "\"version\": \"\""));
        bool refused;
        try { Workload.Load(unversioned); refused = false; } catch (InvalidDataException) { refused = true; }
        finally { File.Delete(unversioned); }
        Expect("a workload file without a version is refused", refused);
    }

    static bool Is(TaskTiming t, int queue, int tool, int ttft, int gen, int total) =>
        t.Queue.TotalMilliseconds == queue && t.Tool.TotalMilliseconds == tool && t.Ttft?.TotalMilliseconds == ttft
        && t.Generation?.TotalMilliseconds == gen && t.Total.TotalMilliseconds == total;

    static string Clocks(TaskTiming t) =>
        $"queue {t.Queue.TotalMilliseconds:F0}, tool {t.Tool.TotalMilliseconds:F0}, ttft {t.Ttft?.TotalMilliseconds:F0}, " +
        $"generation {t.Generation?.TotalMilliseconds:F0}, total {t.Total.TotalMilliseconds:F0} ({t.Outcome})";

    static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}
