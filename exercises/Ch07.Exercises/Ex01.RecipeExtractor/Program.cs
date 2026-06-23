// ============================================================================
// Exercise 01 — Recipe Extractor (Beginner)
// Given free text describing a recipe, extract structured data using
// RunAsync<T>() for typed responses. FULLY STANDALONE.
// ============================================================================

using System.ClientModel;
using System.Text.Json;
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

ChatClientAgent agent = new(
    chatClient,
    instructions: """
        You are a recipe parser. Extract structured recipe data from the provided text.
        Return ONLY valid JSON matching the requested schema. Do not include any extra text.
        If a field cannot be determined, use null for optional fields or an empty array for lists.
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
        AgentSession session = await agent.CreateSessionAsync(cts.Token);
        AgentResponse<Recipe> response = await agent.RunAsync<Recipe>(
            $"Extract the recipe from this text:\n\n{recipeText}",
            session,
            cancellationToken: cts.Token);

        if (response.Result is { } recipe)
        {
            Console.WriteLine("--- Extracted Recipe ---");
            Console.WriteLine($"  Name: {recipe.RecipeName ?? "Unknown"}");
            Console.WriteLine($"  Prep Time: {recipe.PrepTime ?? "N/A"}");
            Console.WriteLine($"  Cook Time: {recipe.CookTime ?? "N/A"}");
            Console.WriteLine($"  Ingredients ({recipe.Ingredients?.Count ?? 0}):");
            foreach (string ingredient in recipe.Ingredients ?? [])
            {
                Console.WriteLine($"    - {ingredient}");
            }
            Console.WriteLine($"  Steps ({recipe.Steps?.Count ?? 0}):");
            int step = 0;
            foreach (string s in recipe.Steps ?? [])
            {
                step++;
                Console.WriteLine($"    {step}. {s}");
            }
        }
        else
        {
            Console.WriteLine("  (No structured result returned)");
            Console.WriteLine($"  Raw: {response.Text}");
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
// Data model
// ---------------------------------------------------------------

/// <summary>
/// Represents structured recipe data extracted from free text.
/// </summary>
sealed class Recipe
{
    /// <summary>Name of the recipe.</summary>
    public string? RecipeName { get; set; }

    /// <summary>List of ingredients.</summary>
    public List<string>? Ingredients { get; set; }

    /// <summary>Ordered list of preparation steps.</summary>
    public List<string>? Steps { get; set; }

    /// <summary>Preparation time (e.g., "15 minutes").</summary>
    public string? PrepTime { get; set; }

    /// <summary>Cooking time (e.g., "45 minutes").</summary>
    public string? CookTime { get; set; }
}
