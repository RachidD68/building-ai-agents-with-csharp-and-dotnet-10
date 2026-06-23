// ============================================================================
// Exercise 02 — Prompt Template Engine (Intermediate)
// Define templates with {placeholder} variables, fill them, and display the
// final prompt with estimated token count.
// ============================================================================

using System.Text.RegularExpressions;

Console.WriteLine("=== Exercise: Prompt Template Engine ===\n");

// ---------------------------------------------------------------
// Step 1: Define reusable prompt templates
// ---------------------------------------------------------------
PromptTemplate codeReview = new(
    "CodeReview",
    """
    You are a senior {language} developer performing a code review.
    Review the following code for {aspects}.
    Provide specific, actionable feedback.

    Code:
    ```{language}
    {code}
    ```
    """);

PromptTemplate summarizer = new(
    "Summarizer",
    """
    Summarize the following {content_type} in {style} style.
    Target audience: {audience}.
    Maximum length: {max_length} words.

    Content:
    {content}
    """);

PromptTemplate translator = new(
    "Translator",
    """
    Translate the following text from {source_language} to {target_language}.
    Maintain the {tone} tone of the original.

    Text: {text}
    """);

PromptTemplate[] templates = [codeReview, summarizer, translator];

// ---------------------------------------------------------------
// Step 2: Display available templates and their placeholders
// ---------------------------------------------------------------
Console.WriteLine("--- Available Templates ---\n");

foreach (PromptTemplate template in templates)
{
    string[] placeholders = template.GetPlaceholders();
    Console.WriteLine($"  {template.Name}:");
    Console.WriteLine($"    Placeholders: {string.Join(", ", placeholders)}");
    Console.WriteLine($"    Base tokens (est): {EstimateTokens(template.Template)}");
    Console.WriteLine();
}

// ---------------------------------------------------------------
// Step 3: Fill templates with sample data
// ---------------------------------------------------------------
Console.WriteLine("--- Filled Templates ---\n");

string filledCodeReview = codeReview.Fill(new Dictionary<string, string>
{
    ["language"] = "C#",
    ["aspects"] = "performance, readability, and error handling",
    ["code"] = "public async Task<int> GetCount() => await _db.Items.CountAsync();"
});

DisplayFilledTemplate("CodeReview", filledCodeReview);

string filledSummarizer = summarizer.Fill(new Dictionary<string, string>
{
    ["content_type"] = "technical blog post",
    ["style"] = "executive summary",
    ["audience"] = "C-level executives",
    ["max_length"] = "100",
    ["content"] = "The new .NET 10 release introduces significant improvements to AI integration..."
});

DisplayFilledTemplate("Summarizer", filledSummarizer);

string filledTranslator = translator.Fill(new Dictionary<string, string>
{
    ["source_language"] = "English",
    ["target_language"] = "Spanish",
    ["tone"] = "professional",
    ["text"] = "Our quarterly revenue exceeded expectations by 15%."
});

DisplayFilledTemplate("Translator", filledTranslator);

// ---------------------------------------------------------------
// Step 4: Interactive mode
// ---------------------------------------------------------------
Console.WriteLine("--- Interactive Mode ---\n");
Console.WriteLine("Pick a template (1=CodeReview, 2=Summarizer, 3=Translator, q=quit):");

while (true)
{
    Console.Write("\nChoice: ");
    string? choice = Console.ReadLine()?.Trim();

    if (string.IsNullOrEmpty(choice) || choice.Equals("q", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    PromptTemplate? selected = choice switch
    {
        "1" => codeReview,
        "2" => summarizer,
        "3" => translator,
        _ => null
    };

    if (selected is null)
    {
        Console.WriteLine("Invalid choice. Try 1, 2, 3, or q.");
        continue;
    }

    string[] placeholders = selected.GetPlaceholders();
    Dictionary<string, string> values = [];

    foreach (string placeholder in placeholders)
    {
        Console.Write($"  {placeholder}: ");
        string? value = Console.ReadLine();
        values[placeholder] = value ?? string.Empty;
    }

    string result = selected.Fill(values);
    DisplayFilledTemplate(selected.Name, result);
}

Console.WriteLine("\n=== Exercise complete! ===");

// ---------------------------------------------------------------
// Helper functions
// ---------------------------------------------------------------

/// <summary>
/// Displays a filled template with its estimated token count.
/// </summary>
/// <param name="name">The template name.</param>
/// <param name="filled">The filled template text.</param>
static void DisplayFilledTemplate(string name, string filled)
{
    Console.WriteLine($"  [{name}] ({filled.Length} chars, ~{EstimateTokens(filled)} tokens):");
    Console.WriteLine();

    foreach (string line in filled.Split('\n'))
    {
        Console.WriteLine($"    {line.TrimEnd()}");
    }

    Console.WriteLine();
}

/// <summary>
/// Estimates token count using the 4-characters-per-token heuristic.
/// </summary>
/// <param name="text">The text to estimate.</param>
/// <returns>Estimated token count.</returns>
static int EstimateTokens(string text)
{
    if (string.IsNullOrEmpty(text)) return 0;
    return (int)Math.Ceiling(text.Length / 4.0);
}

// ---------------------------------------------------------------
// Prompt template type
// ---------------------------------------------------------------

/// <summary>
/// A reusable prompt template with <c>{placeholder}</c> variables.
/// </summary>
/// <param name="Name">The template name.</param>
/// <param name="Template">The template string with placeholders.</param>
sealed partial record PromptTemplate(string Name, string Template)
{
    /// <summary>
    /// Extracts all unique placeholder names from the template.
    /// </summary>
    /// <returns>An array of placeholder names.</returns>
    public string[] GetPlaceholders()
    {
        return PlaceholderRegex().Matches(Template)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToArray();
    }

    /// <summary>
    /// Fills the template by replacing all placeholders with provided values.
    /// </summary>
    /// <param name="values">A dictionary mapping placeholder names to values.</param>
    /// <returns>The filled template string.</returns>
    public string Fill(Dictionary<string, string> values)
    {
        string result = Template;
        foreach ((string key, string value) in values)
        {
            result = result.Replace($"{{{key}}}", value);
        }
        return result;
    }

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex PlaceholderRegex();
}
