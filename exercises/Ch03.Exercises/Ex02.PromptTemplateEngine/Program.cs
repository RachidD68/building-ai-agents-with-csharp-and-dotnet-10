// ============================================================================
// Exercise 02 — Prompt Template Engine (Intermediate)
// A small prompt-template class with {placeholder} substitution from a
// dictionary, an estimated token count for every rendered prompt, and a hard
// token cap: if the rendered prompt exceeds the cap, Render throws.
// Tested with three templates — a system prompt, a user question template,
// and a few-shot classification template.
// ============================================================================

using System.Text.RegularExpressions;

Console.WriteLine("=== Exercise: Prompt Template Engine — Budgeted Rendering ===\n");

// ---------------------------------------------------------------
// Step 1: Three templates — system, user question, few-shot
// ---------------------------------------------------------------
PromptTemplate systemPrompt = new(
    "SystemPrompt",
    """
    You are {assistant_name}, a support assistant for {product}.
    Rules:
      - Answer only from the provided documentation.
      - If you don't know, say so and offer to open a ticket.
      - Never invent order numbers or prices.
    """,
    MaxTokens: 120);

PromptTemplate userQuestion = new(
    "UserQuestion",
    """
    Customer tier: {tier}
    Question: {question}
    """,
    MaxTokens: 80);

PromptTemplate fewShotClassifier = new(
    "FewShotClassifier",
    """
    Classify the support message into exactly one category:
    billing, bug, how-to, or feature-request.

    Message: "I was charged twice this month."
    Category: billing

    Message: "The export button crashes the app."
    Category: bug

    Message: "How do I invite a teammate to my workspace?"
    Category: how-to

    Message: "{message}"
    Category:
    """,
    MaxTokens: 160);

PromptTemplate[] templates = [systemPrompt, userQuestion, fewShotClassifier];

Console.WriteLine("--- Templates ---\n");
foreach (PromptTemplate template in templates)
{
    Console.WriteLine($"  {template.Name}:");
    Console.WriteLine($"    Placeholders: {string.Join(", ", template.GetPlaceholders())}");
    Console.WriteLine($"    Hard cap:     {template.MaxTokens} tokens");
    Console.WriteLine();
}

// ---------------------------------------------------------------
// Step 2: Render all three within budget
// ---------------------------------------------------------------
Console.WriteLine("--- Rendered Prompts ---\n");

Display(systemPrompt, systemPrompt.Render(new Dictionary<string, string>
{
    ["assistant_name"] = "Contoso Assistant",
    ["product"] = "Contoso Cloud",
}));

Display(userQuestion, userQuestion.Render(new Dictionary<string, string>
{
    ["tier"] = "pro",
    ["question"] = "How do I rotate my API key?",
}));

Display(fewShotClassifier, fewShotClassifier.Render(new Dictionary<string, string>
{
    ["message"] = "Please add dark mode to the dashboard.",
}));

// ---------------------------------------------------------------
// Step 3: Blow the budget — the hard cap throws
// ---------------------------------------------------------------
// A prompt blows its budget the moment someone pastes a wall of text into a
// placeholder. The cap turns a silent cost overrun into a loud exception.
Console.WriteLine("--- Hard Cap ---\n");

string pastedWallOfText = string.Join(' ', Enumerable.Repeat(
    "The app crashed again and here is the full stack trace I copied from the log viewer:", 20));

try
{
    userQuestion.Render(new Dictionary<string, string>
    {
        ["tier"] = "free",
        ["question"] = pastedWallOfText,
    });

    Console.WriteLine("  (unreachable — the render above must throw)");
}
catch (InvalidOperationException ex)
{
    Console.WriteLine($"  Caught: {ex.Message}");
}

Console.WriteLine("\n=== Exercise complete! ===");

// ---------------------------------------------------------------
// Helper
// ---------------------------------------------------------------

/// <summary>
/// Prints a rendered prompt with its estimated token count and budget.
/// </summary>
/// <param name="template">The template that produced the prompt.</param>
/// <param name="rendered">The rendered prompt text.</param>
static void Display(PromptTemplate template, string rendered)
{
    int tokens = PromptTemplate.EstimateTokens(rendered);
    Console.WriteLine($"  [{template.Name}] ~{tokens}/{template.MaxTokens} tokens ({rendered.Length} chars):");
    Console.WriteLine();

    foreach (string line in rendered.Split('\n'))
    {
        Console.WriteLine($"    {line.TrimEnd()}");
    }

    Console.WriteLine();
}

// ---------------------------------------------------------------
// Prompt template type
// ---------------------------------------------------------------

/// <summary>
/// A reusable prompt template with <c>{placeholder}</c> variables and a hard
/// token cap enforced at render time.
/// </summary>
/// <param name="Name">The template name.</param>
/// <param name="Template">The template string with placeholders.</param>
/// <param name="MaxTokens">The hard cap on the rendered prompt's estimated tokens.</param>
sealed partial record PromptTemplate(string Name, string Template, int MaxTokens)
{
    /// <summary>
    /// Extracts all unique placeholder names from the template.
    /// </summary>
    /// <returns>An array of placeholder names.</returns>
    public string[] GetPlaceholders()
    {
        return [.. PlaceholderRegex().Matches(Template)
            .Select(m => m.Groups[1].Value)
            .Distinct()];
    }

    /// <summary>
    /// Renders the template by substituting every placeholder from the
    /// dictionary — the one-line Regex.Replace trick — then enforces the hard
    /// cap: if the rendered prompt's estimated token count exceeds
    /// <see cref="MaxTokens"/>, this method throws.
    /// </summary>
    /// <param name="values">A dictionary mapping placeholder names to values.</param>
    /// <returns>The rendered prompt.</returns>
    /// <exception cref="KeyNotFoundException">A placeholder has no value in the dictionary.</exception>
    /// <exception cref="InvalidOperationException">The rendered prompt exceeds the hard token cap.</exception>
    public string Render(IReadOnlyDictionary<string, string> values)
    {
        string rendered = PlaceholderRegex().Replace(Template, match =>
            values.TryGetValue(match.Groups[1].Value, out string? value)
                ? value
                : throw new KeyNotFoundException(
                    $"No value provided for placeholder '{match.Groups[1].Value}' in template '{Name}'."));

        int estimatedTokens = EstimateTokens(rendered);
        if (estimatedTokens > MaxTokens)
        {
            throw new InvalidOperationException(
                $"Template '{Name}' rendered to ~{estimatedTokens} tokens — over its hard cap of {MaxTokens}.");
        }

        return rendered;
    }

    /// <summary>
    /// Estimates token count with the same length / 4 trick as Chapter 3.
    /// </summary>
    /// <param name="text">The text to estimate.</param>
    /// <returns>Estimated token count.</returns>
    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return (int)Math.Ceiling(text.Length / 4.0);
    }

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex PlaceholderRegex();
}
