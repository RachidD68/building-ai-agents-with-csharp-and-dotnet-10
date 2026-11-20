// ============================================================================
// Cost per accepted task
// ----------------------------------------------------------------------------
// Reads a price table (data: currency, effective date, unit, one rate per
// model, tool and runtime) and one or more usage logs (the UsageDetails each
// call reported, plus every task's outcome), then prints what was spent per
// call and per ACCEPTED task. The second number is the one Contoso delivers;
// it is undefined when nothing was accepted.
//
// The bundled tables and fixtures are ARTIFICIAL (currency XTS, the ISO code
// reserved for testing). They show the arithmetic; they are not prices.
//
//   dotnet run                                   both fixtures, table A
//   dotnet run -- --prices data/prices.artificial-b.json
//   dotnet run -- --usage <usage.json>           e.g. a log written by LoadProfile
//   dotnet run -- --self-test                    offline checks, RESULT n/n
// ============================================================================

using Ch22.CostModel;

if (args.Contains("--self-test")) return await CostModelSelfTest.RunAsync();

string data = Path.Combine(AppContext.BaseDirectory, "data");
PriceTable prices = PriceTable.Load(Option("--prices") ?? Path.Combine(data, "prices.artificial-a.json"));
string[] logs = Option("--usage") is { } one ? [one]
    : [Path.Combine(data, "usage.quiet-morning.json"), Path.Combine(data, "usage.busy-morning.json")];

Console.WriteLine("=== Cost per accepted task ===\n");
Console.WriteLine($"Price table: {prices.Id}, {prices.Currency}, effective {prices.EffectiveDate:yyyy-MM-dd}, {prices.Unit}");
Console.WriteLine($"{"Workload",-42} {"Calls",5} {"Accepted",9} {"Per call",18} {"Per accepted task",18}");
foreach (string path in logs)
{
    UsageLog log = UsageLog.Load(path);
    CostReport r = CostCalculator.Compute(prices, log);
    Console.WriteLine($"{log.Workload,-42} {r.Calls,5} {$"{r.Accepted}/{r.Tasks}",9} " +
                      $"{CostReport.Money(r.CostPerCall, r.Currency),18} {CostReport.Money(r.CostPerAcceptedTask, r.Currency),18}");
    if (r.RuntimeCost > 0)
        Console.WriteLine($"{"",-42} runtime {CostReport.Money(r.RuntimeCost, r.Currency)} of total {CostReport.Money(r.Total, r.Currency)}");
    if (!r.Complete)
        Console.WriteLine($"{"",-42} {r.UnmeasuredCalls} call(s) reported no usage: the total is a lower bound");
}
return 0;

string? Option(string name) => Array.IndexOf(args, name) is int i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
