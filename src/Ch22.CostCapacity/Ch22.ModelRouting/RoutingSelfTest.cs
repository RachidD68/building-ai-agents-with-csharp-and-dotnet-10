using Ch22.LoadProfile;
using Microsoft.Extensions.AI;

namespace Ch22.ModelRouting;

/// <summary>Offline: dotnet run -- --self-test. No model, no network.</summary>
internal static class RoutingSelfTest
{
    static int s_passed, s_total;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Routing and failover: self-test ===");

        Console.WriteLine("\n-- Same tasks, same report (scripted tiers, manual clock) --");
        OfflineScenario s = new();
        List<CandidateReport> r = await s.RunAllAsync();
        Console.WriteLine($"    {CandidateReport.Header}");
        foreach (CandidateReport c in r) Console.WriteLine($"    {c.Row()}");
        CandidateReport baseline = r[0], small = r[1], router = r[2], flaky = r[3], meai = r[4], ga = r[5];
        Expect("every candidate ran the same 12 tasks", r.All(c => c.N == 12));
        Expect("model-small alone is cheaper per call but accepts fewer (7/12): rejected", small.Accepted == 7 && small.VerdictAgainst(baseline).StartsWith("reject"));
        Expect("the router's 2 misroutes are counted (H08 sent cheap, H09 sent capable)", router.Misroutes == 2);
        Expect("one misroute costs one accepted task: 11/12, so the router is rejected", router.Accepted == 11 && router.VerdictAgainst(baseline).StartsWith("reject"));
        Expect("without failover, the 429s become 4 lost tasks", flaky.Accepted == 8 && flaky.FailedAttempts == 4);
        Expect("both failovers: 16 attempts, 4 failed, the same accepted count and cost (one contract)",
            meai is { Attempts: 16, FailedAttempts: 4 } && ga is { Attempts: 16, FailedAttempts: 4 }
            && meai.Accepted == ga.Accepted && meai.Cost.CostPerAcceptedTask == ga.Cost.CostPerAcceptedTask);
        Expect("against the same 429s without it, each failover is adopted (10 vs 8 accepted, cheaper per accepted task)",
            meai.VerdictAgainst(flaky) == "adopt" && ga.VerdictAgainst(flaky) == "adopt");
        Expect("a failed attempt with no usage marks the cost as a lower bound", !meai.Cost.Complete && baseline.Cost.Complete);

        Console.WriteLine("\n-- Failover contract (MEAI001 OrderedFailoverChatClient and the GA one) --");
        await ContractAsync(s);

        Console.WriteLine("\n-- One retry owner --");
        List<Attempt> log = [];
        IChatClient alwaysDown = new MeteredChatClient(new TierModel(s.Clock, 0, new(), s.Tasks.ToDictionary(t => t.Prompt), _ => true, failEvery: 1), "model-large", log);
        IChatClient fallback = new MeteredChatClient(s.Small, "model-small", log);
#pragma warning disable MEAI001 // Experimental; the GA alternative is GaFailoverChatClient.
        await new OrderedFailoverChatClient([new RetryingChatClient(alwaysDown, maxRetries: 3), fallback], leaveOpen: true)
            .GetResponseAsync([new(ChatRole.User, s.Tasks[0].Prompt)]);
        int twoOwners = log.Count(a => a.Model == "model-large");
        log.Clear();
        await new OrderedFailoverChatClient([alwaysDown, fallback], leaveOpen: true).GetResponseAsync([new(ChatRole.User, s.Tasks[0].Prompt)]);
#pragma warning restore MEAI001
        int oneOwner = log.Count(a => a.Model == "model-large");
        Console.WriteLine($"    primary attempts per request: retry in the tier + failover = {twoOwners}, failover only = {oneOwner}");
        Expect("DEFECT REPRODUCED  a tier retry under a failover sends 4 attempts to a provider that is down", twoOwners == 4);
        Expect("with the failover as the only owner: 1 attempt, then the fallback", oneOwner == 1);

        Console.WriteLine("\n-- A cache improves latency, not quality --");
        List<Attempt> cacheLog = [];
        DistributedCachingChatClient cached = new(new MeteredChatClient(s.Small, "model-small", cacheLog), new DictionaryCache());
        CandidateReport cold = await Comparison.RunAsync("cached model-small, pass 1", cached, cacheLog, s.Tasks, OfflineScenario.System, s.Prices, s.Clock);
        CandidateReport warm = await Comparison.RunAsync("cached model-small, pass 2", cached, cacheLog, s.Tasks, OfflineScenario.System, s.Prices, s.Clock);
        Console.WriteLine($"    {cold.Row()}\n    {warm.Row()}");
        Expect("pass 2 is served from the cache: no model attempt, p95 0 ms", warm.Attempts == 0 && warm.P95Ms == 0);
        Expect("and accepts exactly as many as pass 1: the cache repeats the wrong answers too", warm.Accepted == cold.Accepted && cold.Accepted == 7);

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    static async Task ContractAsync(OfflineScenario s)
    {
        string prompt = s.Tasks[0].Prompt;
        List<Attempt> log = [];
        IChatClient Down() => new MeteredChatClient(new TierModel(s.Clock, 0, new(), s.Tasks.ToDictionary(t => t.Prompt), _ => true, failEvery: 1), "down", log);
        IChatClient Up() => new MeteredChatClient(s.Small, "up", log);

        using CancellationTokenSource canceled = new();
        canceled.Cancel();
#pragma warning disable MEAI001 // Experimental; the GA alternative is GaFailoverChatClient.
        IChatClient[] meaiAndGa(Func<IChatClient> first) =>
            [new OrderedFailoverChatClient([first(), Up()]), new GaFailoverChatClient(first(), Up())];
        foreach (IChatClient f in meaiAndGa(Down))
        {
            log.Clear();
            ChatResponse ok = await f.GetResponseAsync([new(ChatRole.User, prompt)]);
            Expect($"{Name(f)}: a failure before any output fails over (down, then up)",
                log.Select(a => a.Model).SequenceEqual(["down", "up"]) && ok.Text == "APPROVE");
        }
        foreach (IChatClient f in meaiAndGa(Up))
        {
            log.Clear();
            bool threw = await Throws<OperationCanceledException>(() => f.GetResponseAsync([new(ChatRole.User, prompt)], cancellationToken: canceled.Token));
            Expect($"{Name(f)}: a caller's cancellation is not failed over", threw && log.All(a => a.Model == "up") && log.Count <= 1);
        }
        log.Clear();
        OrderedFailoverChatClient capped = new([Down(), Up()]) { MaximumAttemptsPerRequest = 1 };
        Expect("MEAI001: MaximumAttemptsPerRequest = 1 stops after the first failure",
            await Throws<HttpRequestException>(() => capped.GetResponseAsync([new(ChatRole.User, prompt)])) && log.Count == 1);
        FailsMidStream midStream = new();
        FailsMidStream second = new();
        OrderedFailoverChatClient streaming = new([midStream, second]);
        bool streamThrew = await Throws<HttpRequestException>(async () =>
        {
            await foreach (ChatResponseUpdate _ in streaming.GetStreamingResponseAsync([new(ChatRole.User, prompt)])) { }
        });
        Expect("MEAI001: once streamed output is exposed, a failure propagates; no second generation is spliced in",
            streamThrew && midStream.Calls == 1 && second.Calls == 0);
#pragma warning restore MEAI001
    }

    static string Name(IChatClient c) => c is GaFailoverChatClient ? "GA" : "MEAI001";

    static async Task<bool> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); return false; }
        catch (T) { return true; }
    }

    static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}
