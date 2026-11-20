using Microsoft.Extensions.AI;

namespace Ch22.CostModel;

/// <summary>Offline: dotnet run -- --self-test. No model, no network.</summary>
internal static class CostModelSelfTest
{
    static int s_passed, s_total;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Cost model: self-test ===");
        string data = Path.Combine(AppContext.BaseDirectory, "data");
        PriceTable a = PriceTable.Load(Path.Combine(data, "prices.artificial-a.json"));
        PriceTable b = PriceTable.Load(Path.Combine(data, "prices.artificial-b.json"));
        UsageLog quiet = UsageLog.Load(Path.Combine(data, "usage.quiet-morning.json"));
        UsageLog busy = UsageLog.Load(Path.Combine(data, "usage.busy-morning.json"));

        Console.WriteLine("\n-- Price table as data --");
        Expect("a table without a currency is refused", Throws(() => PriceTable.Parse(TableJson(currency: null))));
        Expect("a table without an effective date is refused", Throws(() => PriceTable.Parse(TableJson(date: null))));
        Expect("a table in another unit is refused", Throws(() => PriceTable.Parse(TableJson(unit: "per-token"))));
        Expect("usage dated before the table takes effect is refused",
            Throws(() => CostCalculator.Compute(a, quiet with { Date = new DateOnly(2026, 10, 31) })));
        Expect("an unknown model is refused, not priced at zero",
            Throws(() => CostCalculator.Compute(a, Log(Call("model-unknown", 100, 10)))));
        Expect("the same usage on table B gives another total (the formula holds no price)",
            CostCalculator.Compute(a, quiet).Total != CostCalculator.Compute(b, quiet).Total);

        Console.WriteLine("\n-- Cost from UsageDetails --");
        // 600 fresh x 0.40 + 1,200 cached x 0.10 + 220 out x 1.60 = 712 per million, + 0.0005 tool
        Expect("quiet fixture: 0.001212 XTS per call, by hand", CostCalculator.Compute(a, quiet).CostPerCall == 0.001212m);
        Expect("cached input is part of the input count, priced once",
            CostCalculator.Compute(a, Log(Call("model-large", 1_000, 0, cached: 1_000))).ModelCost == 0.00075m);
        Expect("reasoning is part of the output count, priced at its own rate when the table has one",
            CostCalculator.Compute(b, Log(Call("model-large", 0, 300, reasoning: 200))).ModelCost == 0.0038m);
        Expect("cached tokens above the input count are refused (a double count)",
            Throws(() => CostCalculator.Compute(a, Log(Call("model-small", 100, 10, cached: 150)))));
        CostReport unmeasured = CostCalculator.Compute(a, Log(new CallRecord("t1", "model-small", 1, Usage: null)));
        Expect("a call with no usage is counted as unmeasured, the total is marked incomplete",
            unmeasured is { UnmeasuredCalls: 1, Complete: false });

        Console.WriteLine("\n-- The unit: cost per accepted task --");
        CostReport q = CostCalculator.Compute(a, quiet), s = CostCalculator.Compute(a, busy);
        Console.WriteLine($"    quiet: {q.Calls} calls, {q.Accepted}/{q.Tasks} accepted, per call {CostReport.Money(q.CostPerCall, "XTS")}, per accepted {CostReport.Money(q.CostPerAcceptedTask, "XTS")}");
        Console.WriteLine($"    busy:  {s.Calls} calls, {s.Accepted}/{s.Tasks} accepted, per call {CostReport.Money(s.CostPerCall, "XTS")}, per accepted {CostReport.Money(s.CostPerAcceptedTask, "XTS")}");
        Expect("DEFECT REPRODUCED  per-call cost is flat while retries and abandonment pile up", q.CostPerCall == s.CostPerCall);
        Expect("cost per accepted task shows it: busy >= 2x quiet", s.CostPerAcceptedTask >= 2 * q.CostPerAcceptedTask);
        CostReport none = CostCalculator.Compute(a, Log(Call("model-small", 100, 10)) with { Tasks = [new("t1", "abandoned")] });
        Expect("nothing accepted: cost per accepted task is undefined, not zero",
            none.CostPerAcceptedTask is null && CostReport.Money(none.CostPerAcceptedTask, "XTS") == "undefined");
        CostReport local = CostCalculator.Compute(a, Log(Call("llama3.2", 1_800, 220)) with
            { RuntimeHours = new Dictionary<string, decimal> { ["workstation"] = 0.25m } });
        Expect("local is not free: zero token rates, 0.25 h of workstation = 0.15 XTS", local is { ModelCost: 0, Total: 0.15m });

        Console.WriteLine("\n-- Budget: reserve, then reconcile (16 concurrent tasks, 0.03 each, limit 0.30) --");
        var naive = new CheckThenSpendBudget(0.30m);
        int naiveRan = await RunConcurrentAsync(16, () => naive.CanAfford(0.03m), () => naive.Spend(0.03m));
        Console.WriteLine($"    check-then-spend: {naiveRan} calls ran, spent {naive.Spent:0.00}");
        Expect("DEFECT REPRODUCED  check-then-spend lets all 16 through and overspends the limit", naive.Spent > naive.Limit);

        var ledger = new BudgetLedger(0.30m);
        Guid?[] held = new Guid?[16];
        int granted = await RunConcurrentAsync(16, i => (held[i] = ledger.TryReserve(0.03m)) is not null,
            i => ledger.Reconcile(held[i]!.Value, 0.025m));
        Console.WriteLine($"    reserve-then-reconcile: {granted} calls ran, spent {ledger.Spent:0.000}, reserved {ledger.Reserved:0.000}");
        Expect("reservations refuse 6 of the 16 before their call: 10 run, the limit holds", granted == 10);
        Expect("reconciliation books the measured cost and releases the rest", ledger is { Spent: 0.25m, Reserved: 0m });
        Guid r = ledger.TryReserve(0.03m)!.Value;
        ledger.Reconcile(r, 0.04m);
        Expect("a cost above its reservation is booked and recorded as an overrun", ledger is { Spent: 0.29m, Overrun: 0.01m });
        Expect("a reservation cannot be reconciled twice", Throws(() => ledger.Reconcile(r, 0.01m)));

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    /// <summary>
    /// n tasks on the thread pool. Each one checks (or reserves), then waits
    /// until every task has checked, as if its model call were still running,
    /// then pays. Returns how many passed the check.
    /// </summary>
    static async Task<int> RunConcurrentAsync(int n, Func<int, bool> check, Action<int> pay)
    {
        int checkedIn = 0, passed = 0;
        TaskCompletionSource allChecked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource callsReturn = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] tasks = [.. Enumerable.Range(0, n).Select(i => Task.Run(async () =>
        {
            bool ok = check(i);
            if (ok) Interlocked.Increment(ref passed);
            if (Interlocked.Increment(ref checkedIn) == n) allChecked.SetResult();
            await callsReturn.Task;
            if (ok) pay(i);
        }))];
        await allChecked.Task;
        callsReturn.SetResult();
        await Task.WhenAll(tasks);
        return passed;
    }

    static Task<int> RunConcurrentAsync(int n, Func<bool> check, Action pay) =>
        RunConcurrentAsync(n, _ => check(), _ => pay());

    static CallRecord Call(string model, long input, long output, long? cached = null, long? reasoning = null) =>
        new("t1", model, 1, new UsageDetails
        {
            InputTokenCount = input, OutputTokenCount = output,
            CachedInputTokenCount = cached, ReasoningTokenCount = reasoning,
        });

    static UsageLog Log(CallRecord call) => new("test", new DateOnly(2026, 11, 12), [call], [new("t1", "accepted")]);

    static string TableJson(string? currency = "XTS", string? date = "2026-11-01", string unit = PriceTable.PerMillionTokens) =>
        $$"""
        { "id": "t", {{(currency is null ? "" : $"\"currency\": \"{currency}\",")}}
          {{(date is null ? "" : $"\"effectiveDate\": \"{date}\",")}} "unit": "{{unit}}",
          "models": { "m": { "input": 1, "cachedInput": 1, "output": 1 } } }
        """;

    static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException or InvalidOperationException) { return true; }
    }

    static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}
