// ============================================================================
// Exercise 01: Cost Calculator (solution)
// ----------------------------------------------------------------------------
// Prices one usage fixture under two artificial price tables. Each fixture
// run is one context strategy (uncached, stable prefix cached, a sliding
// window plus caching) on one model, with the usage it measured per
// conversation and how many of its conversations were accepted.
//
// The formula holds no provider name and no price: rates come from the
// table, token counts from UsageDetails. Reasoning cost is read from the
// measured ReasoningTokenCount (part of OutputTokenCount), never from a
// multiplier. Cost per accepted conversation is "undefined" when none was.
//
//   dotnet run                    both tables, the bundled fixture
//   dotnet run -- --self-test     offline checks, RESULT n/n
// ============================================================================

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
string data = Path.Combine(AppContext.BaseDirectory, "data");
Fixture fixture = Json.Load<Fixture>(Path.Combine(data, "usage.conversations.json"));
Table[] tables = [Json.Load<Table>(Path.Combine(data, "prices.table-1.json")), Json.Load<Table>(Path.Combine(data, "prices.table-2.json"))];

if (args.Contains("--self-test")) return SelfTest.Run(tables, fixture);

Console.WriteLine("=== Exercise 01: Cost Calculator ===\n");
Console.WriteLine($"Fixture: {fixture.Workload}, {fixture.ConversationsPerMonth:N0} conversations a month");
foreach (Table table in tables)
{
    Console.WriteLine($"\n{table.Id} ({table.Currency}, effective {table.EffectiveDate:yyyy-MM-dd}, artificial)");
    Console.WriteLine($"  {"Strategy",-26} {"Model",-8} {"Per conversation",17} {"Monthly",12} {"Accepted",9} {"Per accepted",14}");
    foreach (Run run in fixture.Runs)
    {
        decimal perConversation = Cost.PerConversation(table, run);
        Console.WriteLine($"  {run.Strategy,-26} {run.Model,-8} {perConversation,17:0.000000} " +
                          $"{perConversation * fixture.ConversationsPerMonth,12:0.00} {$"{run.Accepted}/{run.Conversations}",9} " +
                          $"{Cost.Show(Cost.PerAccepted(table, run)),14}");
    }
}
return 0;

static class Cost
{
    /// <summary>Cached input is part of the input count, reasoning part of the output count.</summary>
    public static decimal PerConversation(Table table, Run run)
    {
        Rate rate = table.Models[run.Model];
        UsageDetails u = run.UsagePerConversation;
        long input = u.InputTokenCount ?? 0, cached = u.CachedInputTokenCount ?? 0;
        long output = u.OutputTokenCount ?? 0, reasoning = u.ReasoningTokenCount ?? 0;
        return ((input - cached) * rate.Input + cached * rate.CachedInput
              + (output - reasoning) * rate.Output + reasoning * (rate.ReasoningOutput ?? rate.Output)) / 1_000_000m;
    }

    public static decimal? PerAccepted(Table table, Run run) =>
        run.Accepted == 0 ? null : PerConversation(table, run) * run.Conversations / run.Accepted;

    public static string Show(decimal? value) => value is { } v ? v.ToString("0.000000") : "undefined";
}

static class SelfTest
{
    public static int Run(Table[] tables, Fixture fixture)
    {
        Console.WriteLine("=== Exercise 01: Cost Calculator, self-test ===\n");
        int passed = 0, total = 0;
        void Expect(string name, bool ok) { total++; if (ok) passed++; Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}"); }
        Run uncached = fixture.Runs[0], cached = fixture.Runs[1], windowed = fixture.Runs[2], none = fixture.Runs[4];

        Expect("the totals change with the table (the formula holds no price)",
            Cost.PerConversation(tables[0], uncached) != Cost.PerConversation(tables[1], uncached));
        Expect("table-1, uncached, by hand: 6,400 x 2 + 1,200 x 8 = 0.0224 per conversation",
            Cost.PerConversation(tables[0], uncached) == 0.0224m);
        Expect("caching changes cost, not acceptance (22/25 both)",
            Cost.PerConversation(tables[0], cached) < Cost.PerConversation(tables[0], uncached) && cached.Accepted == uncached.Accepted);
        Expect("the window is cheaper per conversation and accepts fewer (20/25)",
            Cost.PerConversation(tables[0], windowed) < Cost.PerConversation(tables[0], cached) && windowed.Accepted < cached.Accepted);
        Run moreReasoning = uncached with { UsagePerConversation = new UsageDetails { InputTokenCount = 6_400, OutputTokenCount = 2_000, ReasoningTokenCount = 1_600 } };
        Expect("reasoning is read from the measured count: 800 more reasoning tokens on table-2 add 800 x 9 per million",
            Cost.PerConversation(tables[1], moreReasoning) - Cost.PerConversation(tables[1], uncached) == 0.0072m);
        Expect("zero accepted conversations: \"undefined\", not 0",
            Cost.PerAccepted(tables[0], none) is null && Cost.Show(Cost.PerAccepted(tables[0], none)) == "undefined");

        Console.WriteLine($"\nRESULT {passed}/{total}");
        return passed == total ? 0 : 1;
    }
}

sealed record Rate(decimal Input, decimal CachedInput, decimal Output, decimal? ReasoningOutput = null);
sealed record Table(string Id, string Currency, DateOnly EffectiveDate, string Unit, Dictionary<string, Rate> Models);
sealed record Run(string Strategy, string Model, int Conversations, int Accepted, UsageDetails UsagePerConversation);
sealed record Fixture(string Workload, DateOnly Date, int ConversationsPerMonth, List<Run> Runs);

static class Json
{
    static readonly JsonSerializerOptions s_options = new(JsonSerializerDefaults.Web);
    public static T Load<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), s_options)
        ?? throw new InvalidDataException($"{path} is empty.");
}
