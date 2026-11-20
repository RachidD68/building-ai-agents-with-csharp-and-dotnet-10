using System.Text.Json;
using Ch22.CostModel;
using Ch22.LoadProfile;
using Microsoft.Extensions.AI;

namespace Ch22.ModelRouting;

/// <summary>
/// The offline comparison: a capable, slower model with reasoning tokens, a
/// cheap fast one that only solves the easy tasks, and the candidates built
/// from them. Every candidate runs the same fixed task set and is priced from
/// the same artificial table.
/// </summary>
public sealed class OfflineScenario
{
    public const string System = "You triage expense claims. Reply with exactly one word: APPROVE or ESCALATE.";

    public ManualClock Clock { get; } = new();
    public List<Attempt> Attempts { get; } = [];
    public IReadOnlyList<RoutedTask> Tasks { get; }
    public PriceTable Prices { get; }
    public TierModel Large { get; }
    public TierModel FlakyLarge { get; }
    public TierModel Small { get; }

    public OfflineScenario()
    {
        string data = Path.Combine(AppContext.BaseDirectory, "data");
        Tasks = JsonSerializer.Deserialize<List<RoutedTask>>(File.ReadAllText(Path.Combine(data, "tasks.shared.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Prices = PriceTable.Load(Path.Combine(data, "prices.artificial-a.json"));
        Dictionary<string, RoutedTask> byPrompt = Tasks.ToDictionary(t => t.Prompt);
        UsageDetails large = new() { InputTokenCount = 900, OutputTokenCount = 203, ReasoningTokenCount = 200 };
        UsageDetails small = new() { InputTokenCount = 900, OutputTokenCount = 3 };
        Large = new(Clock, 1_200, large, byPrompt, _ => true);
        FlakyLarge = new(Clock, 1_200, large, byPrompt, _ => true, failEvery: 3);
        Small = new(Clock, 300, small, byPrompt, t => !t.Hard);
    }

    /// <summary>A cheap heuristic, wrong on purpose in both directions: H08 is hard but
    /// short and keyword-free; a long easy claim looks hard.</summary>
    public static bool LooksHard(string prompt) =>
        prompt.Contains("no receipt", StringComparison.OrdinalIgnoreCase)
        || prompt.Contains("approval", StringComparison.OrdinalIgnoreCase)
        || prompt.Length > 90;

    public IChatClient Metered(IChatClient tier, string model) => new MeteredChatClient(tier, model, Attempts);

    public async Task<List<CandidateReport>> RunAllAsync()
    {
        List<CandidateReport> reports = [];
        async Task Run(string name, IChatClient candidate, DifficultyRouterChatClient? router = null)
        {
            FlakyLarge.Reset();
            reports.Add(await Comparison.RunAsync(name, candidate, Attempts, Tasks, System, Prices, Clock, router));
        }

        await Run("baseline: model-large only", Metered(Large, "model-large"));
        await Run("model-small only", Metered(Small, "model-small"));
        DifficultyRouterChatClient router = new(Metered(Large, "model-large"), Metered(Small, "model-small"), LooksHard);
        await Run("difficulty router (GA)", router, router);
        await Run("model-large, 429 on every 3rd call", Metered(FlakyLarge, "model-large"));
#pragma warning disable MEAI001 // Experimental in Microsoft.Extensions.AI 10.11.0. GA alternative: GaFailoverChatClient below.
        await Run("failover large->small (MEAI001)",
            new OrderedFailoverChatClient([Metered(FlakyLarge, "model-large"), Metered(Small, "model-small")]));
#pragma warning restore MEAI001
        await Run("failover large->small (GA, by hand)",
            new GaFailoverChatClient(Metered(FlakyLarge, "model-large"), Metered(Small, "model-small")));
        return reports;
    }
}
