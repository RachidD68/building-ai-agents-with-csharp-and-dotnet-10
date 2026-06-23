// ============================================================================
// Chapter 3 — How LLMs Work: A Developer's Mental Model
// Explores tokenization, temperature, embeddings, and cost estimation.
// No network calls — every number below is computed locally.
// ============================================================================

using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;

Console.WriteLine("=== Chapter 3: How LLMs Work — Token Explorer ===\n");

// ----------------------------------------------------------------------------
#region Tokenization Simulation
// ----------------------------------------------------------------------------
// Real LLMs use BPE (Byte Pair Encoding) tokenizers. We simulate the concept
// by splitting text into word-level and subword-level chunks.

WriteSection("Tokenization Simulation");

string[] sampleTexts =
[
    "Hello, world!",
    "The quick brown fox jumps over the lazy dog.",
    "Microsoft.Extensions.AI provides IChatClient for vendor-neutral LLM access.",
    "C# is awesome! 🚀",
    "async Task<ChatResponse> GetCompletionAsync(string prompt, CancellationToken ct)"
];

foreach (string text in sampleTexts)
{
    string[] tokens = SimulateTokenization(text);
    Console.WriteLine($"  Text:   \"{text}\"");
    WriteColored($"  Tokens: [{string.Join("] [", tokens)}]", ConsoleColor.Cyan);
    Console.WriteLine($"  Count:  {tokens.Length} tokens");
    Console.WriteLine($"  Ratio:  ~{(double)text.Length / tokens.Length:F1} chars/token");
    Console.WriteLine();
}

#endregion

// ----------------------------------------------------------------------------
#region Token Count Estimation
// ----------------------------------------------------------------------------
// A common rule of thumb: 1 token ~ 4 characters in English.
// Knowing this helps you estimate costs and stay within context windows.

WriteSection("Token Count Estimation");

Dictionary<string, string> contextExamples = new()
{
    ["Short prompt"] = "What is .NET?",
    ["Medium prompt"] = "Explain the difference between Task and ValueTask in C#. Include code examples and performance considerations.",
    ["System prompt"] = "You are a senior .NET developer assistant. You provide concise, accurate answers about C#, ASP.NET Core, and Azure. Always include code examples when applicable. Format responses using markdown.",
    ["Long document"] = string.Join(' ', Enumerable.Repeat("The .NET platform provides a comprehensive framework for building modern applications.", 20)),
};

foreach ((string label, string text) in contextExamples)
{
    int estimatedTokens = EstimateTokenCount(text);
    Console.WriteLine($"  {label,-16} | {text.Length,5} chars | ~{estimatedTokens,5} tokens | {GetContextPercentage(estimatedTokens)}");
}

Console.WriteLine();
Console.WriteLine("  Context window reference:");
Console.WriteLine("    GPT-4o:      128K tokens");
Console.WriteLine("    GPT-4o-mini: 128K tokens");
Console.WriteLine("    Llama 3.2:     8K tokens");

#endregion

// ----------------------------------------------------------------------------
#region Real Tokenizer (Microsoft.ML.Tokenizers)
// ----------------------------------------------------------------------------
// The length/4 estimate above is fine for back-of-the-envelope work, but when
// precision matters, use a real BPE tokenizer. Microsoft.ML.Tokenizers ships
// TiktokenTokenizer, which matches OpenAI's o200k_base (GPT-4o / GPT-4.1 / GPT-5
// family) and cl100k_base exactly — offline, no network call.

WriteSection("Real Tokenizer vs the length/4 Estimate");

// o200k_base is the encoding used by the current GPT-4o / GPT-4.1 / GPT-5 family.
// The vocab ships in the Microsoft.ML.Tokenizers.Data.O200kBase package.
Tokenizer tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

(string Label, string Text)[] tokenizerSamples =
[
    ("Prose",  "The quick brown fox jumps over the lazy dog and then keeps on running."),
    ("C# code", "public async Task<ChatResponse> GetCompletionAsync(string prompt, CancellationToken ct)"),
    ("JSON",    "{ \"role\": \"user\", \"content\": \"Hello\", \"temperature\": 0.2, \"max_tokens\": 256 }"),
    ("Emoji",   "Ship it! 🚀🔥✅ C# + .NET 10 = 💜"),
];

Console.WriteLine(string.Format("  {0,-8} {1,7} {2,9} {3,8}  {4}",
    "Type", "Real", "len/4", "Error", "Text"));
Console.WriteLine($"  {new string('-', 78)}");

foreach (var (label, text) in tokenizerSamples)
{
    int real = tokenizer.CountTokens(text);
    int estimate = EstimateTokenCount(text);
    double errorPct = real == 0 ? 0 : (double)(estimate - real) / real * 100;

    string preview = text.Length <= 40 ? text : text[..37] + "...";
    ConsoleColor color = Math.Abs(errorPct) switch
    {
        <= 15 => ConsoleColor.Green,
        <= 40 => ConsoleColor.Yellow,
        _ => ConsoleColor.Red
    };

    Console.Write(string.Format("  {0,-8} {1,7} {2,9} ", label, real, estimate));
    WriteColoredInline($"{errorPct,+7:F1}%", color);
    Console.WriteLine($"  {preview}");
}

Console.WriteLine();
Console.WriteLine("  The estimate is close for prose and drifts for code, JSON, and emoji.");
Console.WriteLine("  Use the real tokenizer when a wrong count would overflow the window");
Console.WriteLine("  or under-bill a customer.");

#endregion

// ----------------------------------------------------------------------------
#region Message Array & Cost Growth
// ----------------------------------------------------------------------------
// A chat request is an ordered List<ChatMessage>. "History" is just that array,
// re-sent in full on every turn. Re-counting the whole array each turn shows
// why an unbounded conversation gets expensive — the $2,047 story, in code.
// No network call: the real tokenizer from the region above does the counting.

WriteSection("Message Array & Cost Growth");

List<ChatMessage> conversation =
[
    new ChatMessage(ChatRole.System,
        "You are a terse, senior .NET engineer. Answer with one sentence, then code."),
];

// A scripted multi-turn exchange. Each (user, assistant) pair is one "turn".
(string User, string Assistant)[] turns =
[
    ("What is IAsyncEnumerable<T> for?",
     "Streaming sequences you await item-by-item: await foreach (var x in source) { }"),
    ("How do I cancel it?",
     "Pass a CancellationToken and honor it: [EnumeratorCancellation] on the token param."),
    ("Show a producer.",
     "async IAsyncEnumerable<int> Count() { for (int i=0;i<10;i++){ await Task.Yield(); yield return i; } }"),
    ("And a consumer with a timeout?",
     "using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await foreach (var i in Count().WithCancellation(cts.Token)) Log(i);"),
];

// GPT-4o input rate, per 1M tokens (verify current rate before relying on it).
const decimal inputRatePer1M = 2.50m;

Console.WriteLine("  Each turn re-sends the ENTIRE array. Running input cost per turn:\n");
Console.WriteLine(string.Format("  {0,-5} {1,10} {2,14} {3,14}",
    "Turn", "Msgs", "Input tokens", "Cost this call"));
Console.WriteLine($"  {new string('-', 50)}");

int turnNumber = 0;
foreach (var (user, assistant) in turns)
{
    turnNumber++;
    conversation.Add(new ChatMessage(ChatRole.User, user));

    // The cost of THIS call is the whole array we send up to (and including) the
    // new user message — the assistant reply does not exist yet when we send.
    int inputTokens = CountConversationTokens(conversation, tokenizer);
    decimal callCost = inputTokens * inputRatePer1M / 1_000_000m;

    Console.WriteLine(string.Format("  {0,-5} {1,10} {2,14:N0} {3,14:C5}",
        turnNumber, conversation.Count, inputTokens, callCost));

    // The model's answer is appended and re-sent on every later turn.
    conversation.Add(new ChatMessage(ChatRole.Assistant, assistant));
}

Console.WriteLine();
Console.WriteLine($"  After {turns.Length} short turns the array holds {conversation.Count} messages.");
Console.WriteLine("  Input tokens grow every turn because the whole history rides along.");
Console.WriteLine("  Now imagine 50 turns of pasted stack traces. That is the $2,047 bill.");

#endregion

// ----------------------------------------------------------------------------
#region Temperature Simulation
// ----------------------------------------------------------------------------
// Temperature controls randomness in token selection.
// Low temperature (0.0) = deterministic; High temperature (1.5) = creative.

WriteSection("Temperature Simulation");

// Simulated probability distribution for next token after "The weather is"
Dictionary<string, double> baseProbs = new()
{
    ["sunny"] = 0.35,
    ["cloudy"] = 0.25,
    ["rainy"] = 0.15,
    ["cold"] = 0.10,
    ["beautiful"] = 0.08,
    ["unpredictable"] = 0.04,
    ["magnificent"] = 0.02,
    ["transformative"] = 0.01,
};

double[] temperatures = [0.0, 0.3, 0.7, 1.0, 1.5];

Console.WriteLine("  Prompt: \"The weather is ___\"");
Console.WriteLine("  Base probabilities:");
foreach ((string token, double prob) in baseProbs)
{
    string bar = new('#', (int)(prob * 40));
    Console.WriteLine($"    {token,-17} {prob:P0}  {bar}");
}
Console.WriteLine();

foreach (double temperature in temperatures)
{
    Console.Write($"  Temperature {temperature:F1}: ");
    Dictionary<string, double> adjusted = ApplyTemperature(baseProbs, temperature);

    // Sample 10 times to show the effect
    string[] samples = new string[10];
    Random rng = new(42); // Fixed seed for reproducibility
    for (int i = 0; i < 10; i++)
    {
        samples[i] = SampleToken(adjusted, rng);
    }

    WriteColored($"[{string.Join(", ", samples)}]", ConsoleColor.Yellow);

    // Show top token probability
    var top = adjusted.OrderByDescending(kv => kv.Value).First();
    Console.WriteLine($"              Top token \"{top.Key}\" has {top.Value:P0} probability");
}

#endregion

// ----------------------------------------------------------------------------
#region Cosine Similarity
// ----------------------------------------------------------------------------
// Embeddings represent text as vectors. Cosine similarity measures how
// "close" two pieces of text are in meaning.

WriteSection("Cosine Similarity (Embedding Distance)");

// Simulated embedding vectors for sentences
// In reality, each dimension captures a semantic feature.
(string Label, double[] Vector)[] sentences =
[
    ("C# is a programming language",   [0.8, 0.1, 0.3, 0.9, 0.2]),
    ("Java is a programming language",  [0.7, 0.1, 0.3, 0.8, 0.2]),
    ("The weather is sunny today",      [0.1, 0.9, 0.1, 0.0, 0.8]),
    ("It is raining outside",           [0.1, 0.8, 0.2, 0.0, 0.7]),
    ("I love writing .NET code",        [0.9, 0.0, 0.4, 0.8, 0.1]),
];

Console.WriteLine("  Pairwise cosine similarity:");
Console.WriteLine(string.Format("  {0,3} {1,-35} {2,-35} {3,10}", "", "Sentence A", "Sentence B", "Similarity"));
Console.WriteLine($"  {new string('-', 85)}");

for (int i = 0; i < sentences.Length; i++)
{
    for (int j = i + 1; j < sentences.Length; j++)
    {
        double similarity = CosineSimilarity(sentences[i].Vector, sentences[j].Vector);
        ConsoleColor color = similarity switch
        {
            > 0.9 => ConsoleColor.Green,
            > 0.7 => ConsoleColor.Yellow,
            _ => ConsoleColor.Red
        };

        Console.Write($"  {"",-3} {sentences[i].Label,-35} {sentences[j].Label,-35} ");
        WriteColored($"{similarity:F4}", color);
    }
}

Console.WriteLine();
Console.WriteLine("  Interpretation:");
Console.WriteLine("    > 0.90 = Very similar (same topic, near-synonyms)");
Console.WriteLine("    > 0.70 = Related (same domain)");
Console.WriteLine("    < 0.70 = Different topics");

#endregion

// ----------------------------------------------------------------------------
#region Token Cost Calculator
// ----------------------------------------------------------------------------
// Understanding token costs is essential for budgeting AI applications.

WriteSection("Token Cost Calculator");

(string Model, decimal InputPer1M, decimal OutputPer1M)[] pricing =
[
    ("GPT-4o",       2.50m,  10.00m),
    ("GPT-4o-mini",  0.15m,   0.60m),
    ("GPT-4.1",      2.00m,   8.00m),
    ("GPT-4.1-mini", 0.40m,   1.60m),
    ("GPT-4.1-nano", 0.10m,   0.40m),
    ("Ollama local", 0.00m,   0.00m),
];

// Scenario: 100 requests/day, average 500 input + 200 output tokens each
int requestsPerDay = 100;
int avgInputTokens = 500;
int avgOutputTokens = 200;
int daysPerMonth = 30;

Console.WriteLine($"  Scenario: {requestsPerDay} requests/day, {avgInputTokens} in + {avgOutputTokens} out tokens each");
Console.WriteLine($"  Monthly volume: {requestsPerDay * daysPerMonth:N0} requests\n");

string header = string.Format("  {0,-16} {1,10} {2,12} {3,11} {4,13}",
    "Model", "Input $/1M", "Output $/1M", "Daily Cost", "Monthly Cost");
Console.WriteLine(header);
Console.WriteLine($"  {new string('-', 65)}");

foreach (var (model, inputRate, outputRate) in pricing)
{
    decimal dailyInputCost = requestsPerDay * avgInputTokens * inputRate / 1_000_000m;
    decimal dailyOutputCost = requestsPerDay * avgOutputTokens * outputRate / 1_000_000m;
    decimal dailyTotal = dailyInputCost + dailyOutputCost;
    decimal monthlyTotal = dailyTotal * daysPerMonth;

    Console.WriteLine(string.Format("  {0,-16} {1,10:F2} {2,12:F2} {3,10:C4} {4,12:C2}",
        model, inputRate, outputRate, dailyTotal, monthlyTotal));
}

// --- Four cost dimensions, not two -------------------------------------------
// Input and output are only half the picture. Cached input and batch are the
// other two levers. Rates below are illustrative mid-2026 GPT-4.1 figures;
// re-verify against the live rate card before you trust them.
//   input   = $2.00 / 1M
//   cached  = $0.50 / 1M  (automatic on >=1,024-token prefixes; 128-token steps)
//   batch   = 50% off both input and output (async, within 24h)
const decimal inRate = 2.00m;     // fresh input
const decimal cachedRate = 0.50m; // cached input (a repeated, stable prefix)
const decimal outRate = 8.00m;    // output

Console.WriteLine();
Console.WriteLine("  Same volume, four pricing dimensions (GPT-4.1 illustrative rates):\n");

// A realistic split: most of the 500 input tokens are a stable system+context
// prefix that caches; only a small tail (the user's new message) is fresh.
int cacheablePrefix = 420;                       // re-sent unchanged every call
int freshTail = avgInputTokens - cacheablePrefix; // the volatile user message
long callsPerMonth = (long)requestsPerDay * daysPerMonth;

decimal uncached = (avgInputTokens * inRate + avgOutputTokens * outRate)
    * callsPerMonth / 1_000_000m;

decimal withCache = (cacheablePrefix * cachedRate + freshTail * inRate + avgOutputTokens * outRate)
    * callsPerMonth / 1_000_000m;

decimal withBatch = uncached * 0.50m; // batch = 50% off the uncached total

Console.WriteLine(string.Format("    {0,-28} {1,12:C2}", "Standard (all fresh)", uncached));
Console.WriteLine(string.Format("    {0,-28} {1,12:C2}", "Cached stable prefix", withCache));
Console.WriteLine(string.Format("    {0,-28} {1,12:C2}", "Batch (50% off)", withBatch));
Console.WriteLine();
Console.WriteLine($"    Caching the {cacheablePrefix}-token prefix saves "
    + $"{(uncached - withCache) / uncached:P0} on this workload.");
Console.WriteLine("    Rule: keep the prefix stable so it caches; put volatile content LAST.");

#endregion

Console.WriteLine("\n=== Token Explorer complete! ===");

// ============================================================================
// Helper Methods
// ============================================================================

/// <summary>
/// Simulates BPE-like tokenization by splitting on whitespace, punctuation,
/// and camelCase/PascalCase boundaries.
/// </summary>
static string[] SimulateTokenization(string text)
{
    List<string> tokens = [];

    // Split on spaces first, then break on punctuation
    string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    foreach (string word in words)
    {
        List<string> subTokens = [];
        string current = "";

        for (int i = 0; i < word.Length; i++)
        {
            char c = word[i];

            // Break on punctuation
            if (char.IsPunctuation(c) || char.IsSymbol(c))
            {
                if (current.Length > 0)
                {
                    subTokens.Add(current);
                    current = "";
                }
                subTokens.Add(c.ToString());
                continue;
            }

            // Break on camelCase boundary (lowercase followed by uppercase)
            if (i > 0 && char.IsUpper(c) && char.IsLower(word[i - 1]))
            {
                if (current.Length > 0)
                {
                    subTokens.Add(current);
                    current = "";
                }
            }

            current += c;
        }

        if (current.Length > 0)
        {
            subTokens.Add(current);
        }

        tokens.AddRange(subTokens);
    }

    return [.. tokens];
}

/// <summary>
/// Estimates token count using the ~4 characters per token rule.
/// </summary>
static int EstimateTokenCount(string text)
{
    return Math.Max(1, (int)Math.Ceiling(text.Length / 4.0));
}

/// <summary>
/// Returns a context window usage label.
/// </summary>
static string GetContextPercentage(int tokens)
{
    double pct = (double)tokens / 128_000 * 100;
    return $"{pct:F3}% of 128K";
}

/// <summary>
/// Applies temperature scaling to a probability distribution.
/// Temperature 0 makes it deterministic (all mass on the top token).
/// Higher temperatures flatten the distribution.
/// </summary>
static Dictionary<string, double> ApplyTemperature(
    Dictionary<string, double> baseProbs,
    double temperature)
{
    if (temperature < 0.01)
    {
        // Temperature ~0: deterministic — put all probability on the top token
        string topToken = baseProbs.OrderByDescending(kv => kv.Value).First().Key;
        return baseProbs.ToDictionary(
            kv => kv.Key,
            kv => kv.Key == topToken ? 1.0 : 0.0);
    }

    // Apply softmax with temperature: p_i = exp(log(p_i)/T) / sum(exp(log(p_j)/T))
    var logScaled = baseProbs.ToDictionary(
        kv => kv.Key,
        kv => Math.Exp(Math.Log(Math.Max(kv.Value, 1e-10)) / temperature));

    double sum = logScaled.Values.Sum();
    return logScaled.ToDictionary(kv => kv.Key, kv => kv.Value / sum);
}

/// <summary>
/// Samples a token from a probability distribution.
/// </summary>
static string SampleToken(Dictionary<string, double> probs, Random rng)
{
    double roll = rng.NextDouble();
    double cumulative = 0;

    foreach ((string token, double prob) in probs.OrderByDescending(kv => kv.Value))
    {
        cumulative += prob;
        if (roll <= cumulative)
        {
            return token;
        }
    }

    return probs.Keys.Last();
}

/// <summary>
/// Computes cosine similarity between two vectors.
/// Returns a value between -1 and 1, where 1 means identical direction.
/// </summary>
static double CosineSimilarity(double[] a, double[] b)
{
    double dot = 0, magA = 0, magB = 0;

    for (int i = 0; i < a.Length; i++)
    {
        dot += a[i] * b[i];
        magA += a[i] * a[i];
        magB += b[i] * b[i];
    }

    double denominator = Math.Sqrt(magA) * Math.Sqrt(magB);
    return denominator == 0 ? 0 : dot / denominator;
}

/// <summary>
/// Prints a section header with formatting.
/// </summary>
static void WriteSection(string title)
{
    Console.WriteLine($"--- {title} ---");
    Console.WriteLine();
}

/// <summary>
/// Writes colored text to the console, then resets the color.
/// </summary>
static void WriteColored(string text, ConsoleColor color)
{
    ConsoleColor original = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.WriteLine(text);
    Console.ForegroundColor = original;
}

/// <summary>
/// Writes colored text without a trailing newline, then resets the color.
/// </summary>
static void WriteColoredInline(string text, ConsoleColor color)
{
    ConsoleColor original = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.Write(text);
    Console.ForegroundColor = original;
}

/// <summary>
/// Counts the tokens an entire message array contributes to a request. Real
/// providers add a few framing tokens per message; we approximate that with a
/// small per-message overhead so the growth trend is realistic.
/// </summary>
static int CountConversationTokens(List<ChatMessage> messages, Tokenizer tokenizer)
{
    const int perMessageOverhead = 4; // role + delimiter framing, approximate
    int total = 0;
    foreach (ChatMessage message in messages)
    {
        total += perMessageOverhead + tokenizer.CountTokens(message.Text);
    }
    return total;
}
