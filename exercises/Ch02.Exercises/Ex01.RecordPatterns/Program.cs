// ============================================================================
// Exercise 01 — Record Patterns (Basic)
// Classifies a list of LlmResponse records into "low-cost", "medium-cost",
// "high-cost", or "error" with a switch expression that combines property
// patterns and relational patterns.
// ============================================================================

Console.WriteLine("=== Exercise: Record Patterns — LlmResponse Cost Classifier ===\n");

// The same LlmResponse/TokenUsage shapes as the companion Ch02.ConsoleApp
// (src/Ch02.CSharpEssentials). The companion's switch stops at low/moderate
// usage; here we extend it with two more branches and a cost threshold.
List<LlmResponse> responses =
[
    new(200, new TokenUsage(120, 80)),       // 200 total    -> low-cost
    new(200, new TokenUsage(310, 145)),      // 455 total    -> low-cost
    new(200, new TokenUsage(900, 400)),      // 1,300 total  -> medium-cost
    new(200, new TokenUsage(1_500, 480)),    // 1,980 total  -> medium-cost
    new(200, new TokenUsage(3_200, 1_800)),  // 5,000 total  -> high-cost
    new(200, new TokenUsage(9_000, 2_500)),  // 11,500 total -> high-cost
    new(429, new TokenUsage(0, 0)),          // rate limited -> error
    new(500, new TokenUsage(0, 0)),          // server error -> error
    new(503, new TokenUsage(0, 0)),          // unavailable  -> error
];

Console.WriteLine($"  {"Status",6} {"In Tok",8} {"Out Tok",8} {"Total",8}   Category");
Console.WriteLine($"  {new string('-', 6)} {new string('-', 8)} {new string('-', 8)} {new string('-', 8)}   {new string('-', 11)}");

foreach (LlmResponse response in responses)
{
    string category = Classify(response);
    Console.WriteLine($"  {response.StatusCode,6} {response.Usage.InputTokens,8:N0} " +
                      $"{response.Usage.OutputTokens,8:N0} {response.Usage.TotalTokens,8:N0}   {category}");
}

// ---------------------------------------------------------------
// Summary: how many responses landed in each category
// ---------------------------------------------------------------
Console.WriteLine("\n--- Category Summary ---\n");

foreach (var group in responses
    .GroupBy(Classify)
    .OrderBy(g => g.Key switch { "low-cost" => 0, "medium-cost" => 1, "high-cost" => 2, _ => 3 }))
{
    Console.WriteLine($"  {group.Key,-12} {group.Count()} response(s)");
}

Console.WriteLine("\n=== Exercise complete! ===");

// ---------------------------------------------------------------
// The classifier — property patterns + relational patterns
// ---------------------------------------------------------------

/// <summary>
/// Classifies an <see cref="LlmResponse"/> by status code and token usage.
/// Property patterns match on <c>StatusCode</c> and the nested
/// <c>Usage.TotalTokens</c>; relational patterns supply the cost thresholds.
/// </summary>
/// <param name="response">The response to classify.</param>
/// <returns>"low-cost", "medium-cost", "high-cost", or "error".</returns>
static string Classify(LlmResponse response) => response switch
{
    { StatusCode: 200, Usage.TotalTokens: < 500 } => "low-cost",
    { StatusCode: 200, Usage.TotalTokens: >= 500 and < 2_000 } => "medium-cost",
    { StatusCode: 200, Usage.TotalTokens: >= 2_000 } => "high-cost",
    _ => "error"   // 429, 5xx, or anything else that is not a clean 200
};

// ---------------------------------------------------------------
// Records — the same shapes as the companion Ch02.ConsoleApp
// ---------------------------------------------------------------

/// <summary>
/// Token usage information from an LLM response.
/// </summary>
/// <param name="InputTokens">Number of tokens in the prompt.</param>
/// <param name="OutputTokens">Number of tokens in the completion.</param>
sealed record TokenUsage(int InputTokens, int OutputTokens)
{
    /// <summary>Gets the total token count.</summary>
    public int TotalTokens => InputTokens + OutputTokens;
}

/// <summary>
/// Simulated LLM API response with status code and usage data.
/// </summary>
/// <param name="StatusCode">HTTP status code.</param>
/// <param name="Usage">Token usage details.</param>
sealed record LlmResponse(int StatusCode, TokenUsage Usage);
