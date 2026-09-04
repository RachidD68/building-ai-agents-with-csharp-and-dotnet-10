// ============================================================================
// Exercise 03 — Conversation Cost Simulator (Challenge)
// ----------------------------------------------------------------------------
// Takes a message array and a daily volume, then prints monthly cost three ways:
//   1. Uncached            — the whole history re-sent fresh every turn.
//   2. Stable-prefix cache — the unchanging prefix billed at a cached discount.
//   3. Windowed + cache    — a sliding window caps history, then caching applies.
//
// It also models reasoning models (hidden reasoning tokens billed at the output
// rate via a per-model multiplier) and prints a comparative table sorted from
// cheapest to most expensive.
//
// No network calls — everything is arithmetic over a token-counted message array.
// ============================================================================

Console.WriteLine("=== Exercise 03: Conversation Cost Simulator ===\n");

const int PerMessageOverhead = 4; // approximate role/delimiter framing per message

// ---------------------------------------------------------------------------
// Model catalogue. ReasoningMultiplier is the hidden-reasoning-token factor:
// 0.0 for ordinary models, ~5.0 for a reasoning model (5x the visible output,
// billed at the output rate). Rates are illustrative mid-2026 figures — verify
// against the live rate card before trusting them.
// ---------------------------------------------------------------------------
ModelPricing[] catalogue =
[
    new("GPT-6 Astra",   InputPer1M: 10.00m, OutputPer1M: 50.00m, CachedInputPer1M: 1.00m, ReasoningMultiplier: 0.0),
    new("GPT-5.6 Sol",   InputPer1M:  4.00m, OutputPer1M: 20.00m, CachedInputPer1M: 0.40m, ReasoningMultiplier: 0.0),
    new("GPT-5.6 Terra", InputPer1M:  2.00m, OutputPer1M: 12.00m, CachedInputPer1M: 0.20m, ReasoningMultiplier: 0.0),
    new("GPT-5.6 Luna",  InputPer1M:  0.20m, OutputPer1M:  1.20m, CachedInputPer1M: 0.02m, ReasoningMultiplier: 0.0),
    new("o4-mini",      InputPer1M: 1.10m, OutputPer1M:  4.40m, CachedInputPer1M: 0.275m, ReasoningMultiplier: 5.0),
];

// ---------------------------------------------------------------------------
// A scripted conversation. Each turn is a (user, assistant) pair; the system
// message is the stable prefix that a real provider would cache.
// ---------------------------------------------------------------------------
const string systemPrompt =
    "You are a meticulous senior .NET engineer. Answer concisely, prefer modern " +
    "C# 14 idioms, always include a short code example, and refuse non-technical " +
    "questions. Keep responses under 200 words unless asked to elaborate.";

(string User, string Assistant)[] turns =
[
    ("How do I read a file asynchronously?", "Use File.ReadAllTextAsync(path, ct)."),
    ("How do I parse JSON?", "JsonSerializer.Deserialize<T>(json) from System.Text.Json."),
    ("How do I cancel a long call?", "Pass a CancellationToken and check ct.ThrowIfCancellationRequested()."),
    ("How do I retry on failure?", "Use Microsoft.Extensions.Http.Resilience or Polly's ResiliencePipeline."),
    ("How do I log structured data?", "ILogger with message templates: logger.LogInformation(\"User {Id}\", id)."),
    ("How do I write a unit test?", "xUnit [Fact], arrange-act-assert, assert with Assert.Equal."),
    ("How do I stream results?", "Return IAsyncEnumerable<T> and await foreach over it."),
    ("How do I cache a value?", "IMemoryCache.GetOrCreateAsync(key, factory)."),
];

// Conversation parameters.
const int avgOutputTokens = 120;   // visible answer length, per turn
const int requestsPerDay = 500;    // daily conversation volume
const int daysPerMonth = 30;
const int windowTurns = 3;         // sliding window: keep system + last N turns

long callsPerMonth = (long)requestsPerDay * daysPerMonth;

// ---------------------------------------------------------------------------
// Build the full message array once, then measure the three strategies.
// ---------------------------------------------------------------------------
List<Message> full = [new(Role.System, systemPrompt)];
foreach (var (user, assistant) in turns)
{
    full.Add(new(Role.User, user));
    full.Add(new(Role.Assistant, assistant));
}

int prefixTokens = EstimateTokens(systemPrompt) + PerMessageOverhead;
int fullInputTokens = CountTokens(full);
int windowedInputTokens = CountTokens(ApplyWindow(full, windowTurns));

Console.WriteLine($"  Conversation: {turns.Length} turns, {full.Count} messages total.");
Console.WriteLine($"  Stable prefix (system): {prefixTokens} tokens.");
Console.WriteLine($"  Full input tokens (last turn): {fullInputTokens}.");
Console.WriteLine($"  Windowed input tokens (system + last {windowTurns} turns): {windowedInputTokens}.");
Console.WriteLine($"  Volume: {requestsPerDay}/day x {daysPerMonth} days = {callsPerMonth:N0} calls/month.\n");

// ---------------------------------------------------------------------------
// Three strategies for GPT-5.6 Terra, the worked headline example.
// ---------------------------------------------------------------------------
ModelPricing headline = catalogue.First(m => m.Name == "GPT-5.6 Terra");

decimal uncached = MonthlyCost(headline, fullInputTokens, cachedPrefix: 0, avgOutputTokens, callsPerMonth);
decimal cached = MonthlyCost(headline, fullInputTokens, cachedPrefix: prefixTokens, avgOutputTokens, callsPerMonth);
decimal windowed = MonthlyCost(headline, windowedInputTokens, cachedPrefix: prefixTokens, avgOutputTokens, callsPerMonth);

Console.WriteLine($"  Monthly cost on {headline.Name} (visible output {avgOutputTokens} tok/turn):\n");
Console.WriteLine(string.Format("    {0,-26} {1,12}", "Strategy", "Monthly $"));
Console.WriteLine($"    {new string('-', 40)}");
Console.WriteLine(string.Format("    {0,-26} {1,12:C2}", "Uncached", uncached));
Console.WriteLine(string.Format("    {0,-26} {1,12:C2}", "Stable-prefix cached", cached));
Console.WriteLine(string.Format("    {0,-26} {1,12:C2}", "Windowed + cached", windowed));
Console.WriteLine();
Console.WriteLine($"    Caching alone saves {(uncached - cached) / uncached:P0}.");
Console.WriteLine($"    Windowing + caching saves {(uncached - windowed) / uncached:P0} vs uncached.\n");

// ---------------------------------------------------------------------------
// Comparative table: windowed + cached monthly cost for every model, sorted
// cheapest first. Reasoning models pay extra for hidden reasoning tokens.
// ---------------------------------------------------------------------------
Console.WriteLine("  All models, windowed + cached, sorted cheapest first:\n");
Console.WriteLine(string.Format("    {0,-14} {1,12} {2,14}", "Model", "Monthly $", "Reasoning x"));
Console.WriteLine($"    {new string('-', 44)}");

var ranked = catalogue
    .Select(m => (Model: m,
                  Cost: MonthlyCost(m, windowedInputTokens, prefixTokens, avgOutputTokens, callsPerMonth)))
    .OrderBy(r => r.Cost);

foreach (var (model, cost) in ranked)
{
    Console.WriteLine(string.Format("    {0,-14} {1,12:C2} {2,14:F1}",
        model.Name, cost, model.ReasoningMultiplier));
}

Console.WriteLine("\n=== Exercise complete! ===");

// ============================================================================
// Cost model
// ============================================================================

/// <summary>
/// Monthly cost for a model given the per-call input token count, how many of
/// those input tokens are a cached stable prefix, the visible output tokens per
/// call, and the number of calls per month. Reasoning models add hidden
/// reasoning tokens (ReasoningMultiplier x visible output) billed at the output
/// rate.
/// </summary>
static decimal MonthlyCost(
    ModelPricing model,
    int inputTokens,
    int cachedPrefix,
    int visibleOutputTokens,
    long callsPerMonth)
{
    int freshInput = Math.Max(0, inputTokens - cachedPrefix);

    decimal inputCost = freshInput * model.InputPer1M + cachedPrefix * model.CachedInputPer1M;

    // Visible output plus hidden reasoning tokens, both at the output rate.
    decimal reasoningTokens = (decimal)model.ReasoningMultiplier * visibleOutputTokens;
    decimal outputCost = (visibleOutputTokens + reasoningTokens) * model.OutputPer1M;

    decimal perCall = (inputCost + outputCost) / 1_000_000m;
    return perCall * callsPerMonth;
}

/// <summary>
/// Applies a sliding window: always keep the system message, then keep only the
/// last <paramref name="keepTurns"/> user/assistant pairs.
/// </summary>
static List<Message> ApplyWindow(List<Message> messages, int keepTurns)
{
    List<Message> system = messages.Where(m => m.Role == Role.System).ToList();
    List<Message> rest = messages.Where(m => m.Role != Role.System).ToList();

    int keepMessages = keepTurns * 2; // each turn = user + assistant
    if (rest.Count > keepMessages)
    {
        rest = rest.Skip(rest.Count - keepMessages).ToList();
    }

    return [.. system, .. rest];
}

/// <summary>Sums estimated tokens across a message array, with per-message overhead.</summary>
static int CountTokens(List<Message> messages)
{
    int total = 0;
    foreach (Message m in messages)
    {
        total += PerMessageOverhead + EstimateTokens(m.Content);
    }
    return total;
}

/// <summary>Estimates token count with the 4-characters-per-token heuristic.</summary>
static int EstimateTokens(string text)
{
    if (string.IsNullOrEmpty(text)) return 0;
    return Math.Max(1, (int)Math.Ceiling(text.Length / 4.0));
}

// ============================================================================
// Types
// ============================================================================

enum Role { System, User, Assistant }

/// <summary>One message in the conversation array.</summary>
sealed record Message(Role Role, string Content);

/// <summary>
/// Per-model pricing. CachedInputPer1M is the discounted rate for a repeated,
/// stable prefix; ReasoningMultiplier models hidden reasoning tokens (0 for
/// ordinary models). All rates are per 1,000,000 tokens.
/// </summary>
sealed record ModelPricing(
    string Name,
    decimal InputPer1M,
    decimal OutputPer1M,
    decimal CachedInputPer1M,
    double ReasoningMultiplier);
