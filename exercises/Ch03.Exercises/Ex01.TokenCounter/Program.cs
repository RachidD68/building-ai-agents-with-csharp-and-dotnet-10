// ============================================================================
// Exercise 01 — Token Counter (Beginner)
// Estimates token counts using the 4-chars-per-token heuristic and calculates
// estimated cost for GPT-4o.
// ============================================================================

Console.WriteLine("=== Exercise: Token Counter — Estimate LLM Usage ===\n");

// GPT-4o pricing (approximate, per 1M tokens)
const decimal InputCostPerMillionTokens = 2.50m;
const decimal OutputCostPerMillionTokens = 10.00m;
// Heuristic: ~4 characters per token (used in EstimateTokens below)

Console.WriteLine("Enter text to estimate token usage (type 'quit' to exit):\n");

while (true)
{
    Console.Write("You: ");
    string? input = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(input) || input.Equals("quit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    // ---------------------------------------------------------------
    // Estimate token counts
    // ---------------------------------------------------------------
    int inputTokens = EstimateTokens(input);

    // Simulate a typical assistant response (roughly 2x input for estimation)
    int estimatedOutputTokens = inputTokens * 2;
    int totalTokens = inputTokens + estimatedOutputTokens;

    // ---------------------------------------------------------------
    // Calculate estimated cost
    // ---------------------------------------------------------------
    decimal inputCost = inputTokens * InputCostPerMillionTokens / 1_000_000m;
    decimal outputCost = estimatedOutputTokens * OutputCostPerMillionTokens / 1_000_000m;
    decimal totalCost = inputCost + outputCost;

    // ---------------------------------------------------------------
    // Display results
    // ---------------------------------------------------------------
    Console.WriteLine();
    Console.WriteLine($"  Input text length : {input.Length} characters");
    Console.WriteLine($"  Input tokens (est): {inputTokens}");
    Console.WriteLine($"  Output tokens (est): {estimatedOutputTokens} (assuming 2x input)");
    Console.WriteLine($"  Total tokens (est) : {totalTokens}");
    Console.WriteLine();
    Console.WriteLine($"  Estimated cost (GPT-4o):");
    Console.WriteLine($"    Input  : ${inputCost:F6}");
    Console.WriteLine($"    Output : ${outputCost:F6}");
    Console.WriteLine($"    Total  : ${totalCost:F6}");
    Console.WriteLine();

    // ---------------------------------------------------------------
    // Context window visualization
    // ---------------------------------------------------------------
    const int Gpt4oContextWindow = 128_000;
    double usagePercent = (double)totalTokens / Gpt4oContextWindow * 100;
    int barLength = Math.Min((int)(usagePercent / 2), 50);
    string bar = new string('=', barLength) + new string('-', 50 - barLength);

    Console.WriteLine($"  Context window usage (GPT-4o 128K):");
    Console.WriteLine($"    [{bar}] {usagePercent:F4}%");
    Console.WriteLine();
}

Console.WriteLine("=== Exercise complete! ===");

// ---------------------------------------------------------------
// Helper
// ---------------------------------------------------------------

/// <summary>
/// Estimates the token count for a given text using the 4-chars-per-token heuristic.
/// </summary>
/// <param name="text">The text to estimate tokens for.</param>
/// <returns>The estimated number of tokens.</returns>
static int EstimateTokens(string text)
{
    if (string.IsNullOrEmpty(text)) return 0;
    return (int)Math.Ceiling(text.Length / 4.0);
}
