// ============================================================================
// Routing and failover, judged on accepted tasks
// ----------------------------------------------------------------------------
// Every candidate runs the same held-out tasks and gets the same report:
// accepted, attempts (failed ones included), cost per accepted task from the
// artificial price table, p95, and for the router its misroutes against each
// task's true difficulty. A candidate is adopted only if it beats the
// baseline on those same tasks.
//
// Two failovers with one contract: OrderedFailoverChatClient is experimental
// (MEAI001) in Microsoft.Extensions.AI 10.11.0; GaFailoverChatClient is the
// GA alternative written on DelegatingChatClient. The difficulty router is
// GA. Retries have one owner: the failover. No tier retries on its own.
//
//   dotnet run                    offline: scripted tiers on a manual clock
//   dotnet run -- --live          Ollama: llama3.2 alone, then behind a dead
//                                 primary (127.0.0.1:9) with each failover
//   dotnet run -- --self-test     offline checks, RESULT n/n
// ============================================================================

using System.Globalization;
using System.Text.Json;
using Ch22.CostModel;
using Ch22.LoadProfile;
using Ch22.ModelRouting;
using Microsoft.Extensions.AI;
using OllamaSharp;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
if (args.Contains("--self-test")) return await RoutingSelfTest.RunAsync();
if (args.Contains("--live")) return await LiveAsync();

Console.WriteLine("=== Routing and failover on the same tasks (offline, scripted tiers) ===\n");
OfflineScenario scenario = new();
Print(await scenario.RunAllAsync(), scenario.Prices);
return 0;

static void Print(List<CandidateReport> reports, PriceTable prices)
{
    Console.WriteLine($"Price table {prices.Id} ({prices.Currency}, artificial). * = some attempts reported no usage.\n");
    Console.WriteLine(CandidateReport.Header);
    foreach (CandidateReport r in reports) Console.WriteLine(r.Row());
    Console.WriteLine();
    // A failover is judged against the same provider trouble without it; everything else against the baseline.
    CandidateReport? troubled = reports.FirstOrDefault(r => r.Name.Contains("429"));
    foreach (CandidateReport r in reports.Skip(1))
    {
        CandidateReport against = r.Name.Contains("failover") && troubled is not null ? troubled : reports[0];
        Console.WriteLine($"  {r.Name,-36} vs {against.Name}: {r.VerdictAgainst(against)}");
    }
}

static async Task<int> LiveAsync()
{
    Console.WriteLine("=== Failover on the same tasks (live: Ollama llama3.2) ===\n");
    string data = Path.Combine(AppContext.BaseDirectory, "data");
    Workload workload = Workload.Load(Path.Combine(data, "workload.contoso-claims.v1.json"));
    List<RoutedTask> tasks = [.. workload.Tasks.Select(t => new RoutedTask(t.Id, t.Prompt, t.Expected, Hard: false))];
    PriceTable prices = PriceTable.Load(Path.Combine(data, "prices.artificial-a.json"));
    List<Attempt> attempts = [];
    using HttpClient ollama = new() { BaseAddress = new Uri("http://localhost:11434"), Timeout = TimeSpan.FromMinutes(5) };
    using HttpClient dead = new() { BaseAddress = new Uri("http://127.0.0.1:9"), Timeout = TimeSpan.FromSeconds(30) };
    IChatClient Llama() => new MeteredChatClient(new OllamaApiClient(ollama, "llama3.2"), "llama3.2", attempts);
    IChatClient Dead() => new MeteredChatClient(new OllamaApiClient(dead, "llama3.2"), "llama3.2", attempts);
    TimeProvider clock = TimeProvider.System;

    List<CandidateReport> reports =
    [
        await Comparison.RunAsync("baseline: llama3.2 only", Llama(), attempts, tasks, workload.System, prices, clock,
            runtimeHost: "workstation"),
#pragma warning disable MEAI001 // Experimental in Microsoft.Extensions.AI 10.11.0. GA alternative: GaFailoverChatClient.
        await Comparison.RunAsync("dead primary -> llama3.2 (MEAI001)", new OrderedFailoverChatClient([Dead(), Llama()]),
            attempts, tasks, workload.System, prices, clock, runtimeHost: "workstation"),
#pragma warning restore MEAI001
        await Comparison.RunAsync("dead primary -> llama3.2 (GA)", new GaFailoverChatClient(Dead(), Llama()),
            attempts, tasks, workload.System, prices, clock, runtimeHost: "workstation"),
    ];
    Print(reports, prices);
    Console.WriteLine("\n  Token rates for llama3.2 are 0 in the table; the cost is workstation time (artificial rate).");
    Console.WriteLine($"  Failed attempts: {JsonSerializer.Serialize(attempts.Where(a => a.Error is not null).Select(a => a.Error).Distinct())}");
    return 0;
}
