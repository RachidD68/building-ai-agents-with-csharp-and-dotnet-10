// ============================================================================
// Exercise 01 — Recipe Extractor (Basic, solution)
// A typed agent that extracts a Recipe record from pasted-in recipe text:
// title, ingredients as a NESTED list of Ingredient records (name/quantity/
// unit), steps as a list of strings, and total time in MINUTES as an int.
// The nested list shape is exactly Ch05.TypedResponses' TaskList/TaskItem.
// Every property carries a [Description] schema hint. Every ingredient has a
// unit or an explicit null: Unit is [JsonRequired] and nullable. Failures use
// the chapter's TryRunStructuredAsync<T>: one stricter retry, never a loop.
//
//   dotnet run                    three recipes through the model
//   dotnet run -- --self-test     offline checks, fake chat client, RESULT n/n
// ============================================================================

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticAI.Common.Extensions;
using Ch05.RobustExtraction;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

JsonSerializerOptions jsonOptions = new(JsonSerializerOptions.Web);
const string Instructions = """
    You are a recipe parser. Extract structured recipe data from the provided text.
    Return ONLY valid JSON matching the requested schema. Do not include any extra text.
    totalTimeMinutes is the TOTAL time (prep + cook) in minutes, as an integer.
    Each ingredient is an object with name, quantity, and unit. Write the quantity
    as it appears in the text ("500", "3", "half"); set unit to null when the
    ingredient has none (e.g., "3 avocados"), and always include the unit key.
    """;

if (args.Contains("--self-test")) return await SelfTestAsync();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);
using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();
ILogger logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RecipeExtractor");

Console.WriteLine("=== Exercise: Recipe Extractor ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

ChatClientAgent agent = new(chatClient, instructions: Instructions, name: "RecipeParser");

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
    """,
    """
    Pancakes for four, 10 minutes to mix and 20 minutes at the stove. Whisk 250 g
    flour, 2 eggs, 500 ml milk and a pinch of salt into a smooth batter. Melt a knob
    of butter in a hot pan, pour in a ladle of batter, and cook each side until golden.
    """
];

try
{
    foreach (string recipeText in sampleRecipes)
    {
        Console.WriteLine("--- Input Text ---");
        Console.WriteLine(recipeText.Trim());
        Console.WriteLine();

        Result<Recipe> result = await agent.TryRunStructuredAsync<Recipe>(
            $"Extract the recipe from this text:\n\n{recipeText}", jsonOptions, logger, cts.Token);

        switch (result)
        {
            case Success<Recipe> { Value: Recipe recipe }:
                Console.WriteLine("--- Extracted Recipe ---");
                Console.WriteLine($"  Title      : {recipe.Title}");
                Console.WriteLine($"  Total time : {recipe.TotalTimeMinutes} minutes");
                Console.WriteLine($"  Ingredients ({recipe.Ingredients.Count}):");
                foreach (Ingredient ingredient in recipe.Ingredients)
                {
                    string amount = ingredient.Unit is null
                        ? $"{ingredient.Quantity} (no unit)"
                        : $"{ingredient.Quantity} {ingredient.Unit}";
                    Console.WriteLine($"    - {ingredient.Name,-20} {amount}");
                }
                Console.WriteLine($"  Steps ({recipe.Steps.Count}):");
                for (int step = 0; step < recipe.Steps.Count; step++)
                {
                    Console.WriteLine($"    {step + 1}. {recipe.Steps[step]}");
                }
                break;

            case Failure<Recipe> failure:
                Console.WriteLine($"--- No recipe: {string.Join("; ", failure.Messages)}");
                break;
        }
        Console.WriteLine();
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled]");
}

Console.WriteLine("=== Exercise complete! ===");
return 0;

// ---------------------------------------------------------------
// Offline checks: canned replies through the chapter's fake chat client.
// ---------------------------------------------------------------
async Task<int> SelfTestAsync()
{
    Console.WriteLine("=== Recipe Extractor — self-test (offline) ===\n");
    int passed = 0, total = 0;
    async Task<Result<Recipe>> Extract(params string[] replies)
    {
        CannedChatClient client = new([.. replies.Select(CannedChatClient.Stop)]);
        ChatClientAgent fake = new(client, instructions: Instructions);
        return await fake.TryRunStructuredAsync<Recipe>("Extract the recipe.", jsonOptions, NullLogger.Instance);
    }
    void Check(string name, bool ok)
    {
        total++; passed += ok ? 1 : 0;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }

    string[] canned =
    [
        """{"title":"Spaghetti Bolognese","totalTimeMinutes":60,"steps":["Brown the beef."],"ingredients":[{"name":"ground beef","quantity":"500","unit":"g"},{"name":"onion","quantity":"1","unit":null}]}""",
        """{"title":"Guacamole","totalTimeMinutes":5,"steps":["Mash the avocados."],"ingredients":[{"name":"avocado","quantity":"3","unit":null},{"name":"lime","quantity":"2","unit":null}]}""",
        """{"title":"Pancakes","totalTimeMinutes":30,"steps":["Whisk the batter."],"ingredients":[{"name":"flour","quantity":"250","unit":"g"},{"name":"milk","quantity":"500","unit":"ml"}]}"""
    ];
    foreach (string json in canned)
    {
        Result<Recipe> r = await Extract(json);
        Check($"{(r as Success<Recipe>)?.Value.Title ?? "?"}: deserializes, nested ingredients, each with a unit or an explicit null",
            r is Success<Recipe> { Value.Ingredients.Count: 2 });
    }
    Check("an explicit null unit stays null (no empty-string default)",
        await Extract(canned[1]) is Success<Recipe> { Value.Ingredients: [{ Unit: null }, ..] });
    string noUnitKey = canned[0].Replace(",\"unit\":null", "");
    Check("an ingredient without the unit key fails twice -> Failure, not a silent empty unit",
        await Extract(noUnitKey) is Failure<Recipe>);
    Check("an ingredient without the unit key, then a full reply -> Success after one retry",
        await Extract(noUnitKey, canned[0]) is Success<Recipe>);

    Console.WriteLine($"\nRESULT {passed}/{total}");
    return passed == total ? 0 : 1;
}

// ---------------------------------------------------------------
// Data model — nested records, [Description] on every property
// (the shape mirrors Ch05.TypedResponses' TaskList/TaskItem)
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

    // Required but nullable: the key must be present, and null means "no unit"
    // ("3 avocados"). A reply that leaves the key out fails to deserialize.
    [Description("The unit of measure, e.g. 'g', 'cups', 'cloves'; null if the ingredient has none.")]
    [JsonPropertyName("unit")]
    [JsonRequired]
    public string? Unit { get; init; }
}
