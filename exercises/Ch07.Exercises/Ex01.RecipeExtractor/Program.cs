// ============================================================================
// Exercise 01 — Recipe Extractor (Basic)
// A typed agent that extracts a Recipe record from pasted-in recipe text:
// title, ingredients as a NESTED list of Ingredient records (name/quantity/
// unit), steps as a list of strings, and total time in MINUTES as an int.
// The nested list shape is exactly Ch07.TypedResponses' TaskList/TaskItem.
// Every property carries a [Description] schema hint — they survive into the
// schema whether or not the project generates a documentation file. Failures
// use the chapter's recovery pattern: catch JsonException, retry ONCE with a
// stricter prompt on a fresh session, never loop. FULLY STANDALONE.
// ============================================================================

using System.ClientModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

using IHost host = builder.Build();
IConfiguration config = host.Services.GetRequiredService<IConfiguration>();
IChatClient chatClient = CreateChatClient(config);

Console.WriteLine("=== Exercise: Recipe Extractor ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

JsonSerializerOptions jsonOptions = new()
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

ChatClientAgent agent = new(
    chatClient,
    instructions: """
        You are a recipe parser. Extract structured recipe data from the provided text.
        Return ONLY valid JSON matching the requested schema. Do not include any extra text.
        totalTimeMinutes is the TOTAL time (prep + cook) in minutes, as an integer.
        Each ingredient is an object with name, quantity, and unit. Write the quantity
        as it appears in the text ("500", "3", "half"); use an empty string for a
        unit when the ingredient has none (e.g., "3 avocados").
        """,
    name: "RecipeParser");

string[] sampleRecipes =
[
    """
    My grandmother's classic spaghetti bolognese takes about 15 minutes to prep and
    45 minutes to cook. You'll need 500g ground beef, one large onion diced, three
    cloves of garlic minced, a can of crushed tomatoes, two tablespoons of tomato paste,
    a cup of red wine, dried oregano and basil, salt and pepper, and 400g spaghetti.
    First, brown the beef in a large pan. Add onion and garlic, cook until soft. Stir
    in tomato paste, then add crushed tomatoes and wine. Season with herbs, salt and
    pepper. Simmer for 30 minutes. Meanwhile, cook spaghetti according to package
    directions. Serve sauce over pasta with parmesan.
    """,
    """
    Quick guacamole - ready in 5 minutes, no cooking needed! Mash 3 ripe avocados
    in a bowl. Mix in half a diced red onion, one chopped tomato, juice of two limes,
    a handful of fresh cilantro chopped, one jalapeno minced, and salt to taste.
    Stir gently and serve with tortilla chips.
    """
];

foreach (string recipeText in sampleRecipes)
{
    Console.WriteLine("--- Input Text ---");
    Console.WriteLine(recipeText.Trim());
    Console.WriteLine();

    try
    {
        Recipe? recipe = await ExtractRecipeAsync(recipeText);

        if (recipe is not null)
        {
            Console.WriteLine("--- Extracted Recipe ---");
            Console.WriteLine($"  Title      : {recipe.Title}");
            Console.WriteLine($"  Total time : {recipe.TotalTimeMinutes} minutes");

            Console.WriteLine($"  Ingredients ({recipe.Ingredients.Count}):");
            foreach (Ingredient ingredient in recipe.Ingredients)
            {
                string amount = string.IsNullOrWhiteSpace(ingredient.Unit)
                    ? ingredient.Quantity
                    : $"{ingredient.Quantity} {ingredient.Unit}";
                Console.WriteLine($"    - {ingredient.Name,-20} {amount}");
            }

            Console.WriteLine($"  Steps ({recipe.Steps.Count}):");
            int step = 0;
            foreach (string s in recipe.Steps)
            {
                step++;
                Console.WriteLine($"    {step}. {s}");
            }
        }
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n[Cancelled]");
        return;
    }

    Console.WriteLine();
}

Console.WriteLine("=== Exercise complete! ===");

// ---------------------------------------------------------------
// Typed call with the chapter's recovery pattern:
// one attempt, one stricter retry on JsonException, never a loop.
// ---------------------------------------------------------------
async Task<Recipe?> ExtractRecipeAsync(string recipeText)
{
    string prompt = $"Extract the recipe from this text:\n\n{recipeText}";

    // Attempt 1: the ordinary typed call.
    try
    {
        AgentSession session = await agent.CreateSessionAsync(cts.Token);
        AgentResponse<Recipe> response = await agent.RunAsync<Recipe>(
            prompt, session, jsonOptions, cancellationToken: cts.Token);
        return response.Result;
    }
    catch (JsonException ex)
    {
        Console.WriteLine($"  [retry] Typed run failed to deserialize ({ex.Message}); retrying once with a stricter prompt.");
    }

    // Attempt 2: one stricter retry, on a fresh session — a fresh session
    // avoids carrying the malformed exchange forward as context.
    string stricterPrompt =
        prompt +
        "\n\nIMPORTANT: Respond with valid JSON ONLY. No prose, no markdown, no code fences. " +
        "Every field is required. Use the exact field names from the schema.";

    try
    {
        AgentSession retrySession = await agent.CreateSessionAsync(cts.Token);
        AgentResponse<Recipe> retry = await agent.RunAsync<Recipe>(
            stricterPrompt, retrySession, jsonOptions, cancellationToken: cts.Token);
        return retry.Result;
    }
    catch (JsonException ex)
    {
        Console.WriteLine($"\nJSON deserialization error after one retry: {ex.Message}");
        Console.WriteLine("The LLM response did not match the expected schema.");
        Console.WriteLine("This can happen with less capable models. Try a different model or");
        Console.WriteLine("add more explicit schema instructions to the system prompt.");
        return null;
    }
}

// ---------------------------------------------------------------
// Provider factory
// ---------------------------------------------------------------

/// <summary>
/// Creates an <see cref="IChatClient"/> from User Secrets configuration.
/// </summary>
static IChatClient CreateChatClient(IConfiguration configuration)
{
    string provider = configuration["LlmProvider:Provider"]?.ToLowerInvariant() ?? "github";

    return provider switch
    {
        "azure" => new AzureOpenAIClient(
                new Uri(configuration["LlmProvider:AzureOpenAI:Endpoint"] ?? throw new InvalidOperationException("Missing endpoint")),
                new DefaultAzureCredential())
            .GetChatClient(configuration["LlmProvider:AzureOpenAI:Model"] ?? "gpt-4o-mini")
            .AsIChatClient(),

        "openai" => new OpenAI.OpenAIClient(
                configuration["LlmProvider:OpenAI:ApiKey"] ?? throw new InvalidOperationException("Missing key"))
            .GetChatClient(configuration["LlmProvider:OpenAI:Model"] ?? "gpt-4o-mini")
            .AsIChatClient(),

        "github" => new OpenAI.OpenAIClient(
                new ApiKeyCredential(configuration["LlmProvider:GitHub:Token"] ?? throw new InvalidOperationException("Missing token")),
                new OpenAI.OpenAIClientOptions { Endpoint = new Uri(configuration["LlmProvider:GitHub:Endpoint"] ?? "https://models.github.ai/inference") })
            .GetChatClient(configuration["LlmProvider:GitHub:Model"] ?? "openai/gpt-4o-mini")
            .AsIChatClient(),

        "ollama" => new OllamaSharp.OllamaApiClient(
            new Uri(configuration["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"),
            configuration["LlmProvider:Ollama:Model"] ?? "llama3.2"),

        _ => throw new InvalidOperationException($"Unknown provider: '{provider}'")
    };
}

// ---------------------------------------------------------------
// Data model — nested records, [Description] on every property
// (the shape mirrors Ch07.TypedResponses' TaskList/TaskItem)
// ---------------------------------------------------------------

/// <summary>
/// A recipe extracted from free text.
/// </summary>
sealed record Recipe
{
    [Description("The recipe's title or name.")]
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [Description("The ingredients, one entry per ingredient.")]
    [JsonPropertyName("ingredients")]
    public IReadOnlyList<Ingredient> Ingredients { get; init; } = [];

    [Description("The preparation steps, in order.")]
    [JsonPropertyName("steps")]
    public IReadOnlyList<string> Steps { get; init; } = [];

    [Description("Total time (prep + cook) in minutes, as an integer.")]
    [JsonPropertyName("totalTimeMinutes")]
    public int TotalTimeMinutes { get; init; }
}

/// <summary>
/// A single ingredient within a recipe.
/// </summary>
sealed record Ingredient
{
    [Description("The ingredient's name, e.g. 'ground beef'.")]
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [Description("The amount as written in the text, e.g. '500', '3', 'half'.")]
    [JsonPropertyName("quantity")]
    public string Quantity { get; init; } = string.Empty;

    [Description("The unit of measure, e.g. 'g', 'cups', 'cloves'; empty string if none.")]
    [JsonPropertyName("unit")]
    public string Unit { get; init; } = string.Empty;
}
