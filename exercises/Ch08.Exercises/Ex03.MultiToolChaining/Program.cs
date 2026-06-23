// ============================================================================
// Exercise 03 — Multi-Tool Chaining (Challenge)
// Agent with 4+ tools that must chain calls: CurrencyConverter, TaxCalculator,
// ShippingEstimator, PriceFormatter. FULLY STANDALONE.
// ============================================================================

using System.ClientModel;
using System.ComponentModel;
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

Console.WriteLine("=== Exercise: Multi-Tool Chaining ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

AIFunction currencyTool = AIFunctionFactory.Create(ConvertCurrencyAsync);
AIFunction taxTool = AIFunctionFactory.Create(CalculateTaxAsync);
AIFunction shippingTool = AIFunctionFactory.Create(EstimateShippingAsync);
AIFunction formatTool = AIFunctionFactory.Create(FormatPriceAsync);

ChatClientAgent agent = new(
    chatClient,
    instructions: """
        You are a shopping assistant that helps calculate the total cost of purchases
        including currency conversion, tax, and shipping. Use the tools to:
        1. Convert currency if needed
        2. Calculate tax for the destination country
        3. Estimate shipping cost
        4. Format the final total price
        Always break down the costs for the user.
        """,
    name: "ShoppingAssistant",
    tools: [currencyTool, taxTool, shippingTool, formatTool]);

string[] queries =
[
    "What's the total cost of a $50 item shipped to France with tax?",
    "I want to buy a 100 EUR item and ship it to the US. What's the total in USD?",
    "Calculate the total for a 8000 JPY item shipped to UK, show me in GBP."
];

foreach (string query in queries)
{
    Console.WriteLine($"You: {query}\n");

    try
    {
        AgentSession session = await agent.CreateSessionAsync(cts.Token);
        AgentResponse response = await agent.RunAsync(
            query, session, cancellationToken: cts.Token);

        Console.WriteLine($"Agent: {response.Text}\n");
        Console.WriteLine(new string('=', 60) + "\n");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n[Cancelled]");
        return;
    }
}

Console.WriteLine("=== Exercise complete! ===");

// ---------------------------------------------------------------
// Tool implementations
// ---------------------------------------------------------------

/// <summary>Converts an amount from one currency to another.</summary>
[Description("Converts a monetary amount from one currency to another using current exchange rates.")]
static Task<string> ConvertCurrencyAsync(
    [Description("The amount to convert")] double amount,
    [Description("Source currency code (e.g., USD, EUR, GBP, JPY)")] string fromCurrency,
    [Description("Target currency code (e.g., USD, EUR, GBP, JPY)")] string toCurrency,
    CancellationToken cancellationToken)
{
    // Simulated exchange rates relative to USD
    Dictionary<string, double> toUsd = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = 1.0, ["EUR"] = 1.08, ["GBP"] = 1.27,
        ["JPY"] = 0.0067, ["CAD"] = 0.74, ["AUD"] = 0.65
    };

    if (!toUsd.TryGetValue(fromCurrency, out double fromRate) ||
        !toUsd.TryGetValue(toCurrency, out double toRate))
    {
        return Task.FromResult($"Unsupported currency. Supported: {string.Join(", ", toUsd.Keys)}");
    }

    double usdAmount = amount * fromRate;
    double converted = usdAmount / toRate;

    Console.WriteLine($"  [Tool] ConvertCurrency({amount} {fromCurrency} -> {toCurrency}) = {converted:F2}");
    return Task.FromResult($"{amount} {fromCurrency} = {converted:F2} {toCurrency}");
}

/// <summary>Calculates tax for a given amount based on country.</summary>
[Description("Calculates the tax amount for a given price based on the destination country's tax rate.")]
static Task<string> CalculateTaxAsync(
    [Description("The pre-tax amount")] double amount,
    [Description("The destination country (e.g., US, France, UK, Japan, Canada)")] string country,
    CancellationToken cancellationToken)
{
    Dictionary<string, double> taxRates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["US"] = 0.08, ["France"] = 0.20, ["UK"] = 0.20,
        ["Japan"] = 0.10, ["Canada"] = 0.13, ["Germany"] = 0.19
    };

    double rate = taxRates.GetValueOrDefault(country, 0.10);
    double tax = amount * rate;

    Console.WriteLine($"  [Tool] CalculateTax({amount:F2}, {country}) = {tax:F2} ({rate:P0})");
    return Task.FromResult($"Tax for {country} ({rate:P0}): {tax:F2}. After tax: {amount + tax:F2}");
}

/// <summary>Estimates shipping cost based on destination country.</summary>
[Description("Estimates the shipping cost to a destination country based on distance and method.")]
static Task<string> EstimateShippingAsync(
    [Description("The destination country")] string country,
    CancellationToken cancellationToken)
{
    Dictionary<string, double> shippingCosts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["US"] = 5.99, ["France"] = 15.50, ["UK"] = 12.99,
        ["Japan"] = 18.00, ["Canada"] = 8.50, ["Germany"] = 14.00
    };

    double cost = shippingCosts.GetValueOrDefault(country, 20.00);

    Console.WriteLine($"  [Tool] EstimateShipping({country}) = {cost:F2}");
    return Task.FromResult($"Shipping to {country}: ${cost:F2} (standard delivery, 5-10 business days)");
}

/// <summary>Formats a price with currency symbol and proper formatting.</summary>
[Description("Formats a final price amount with the appropriate currency symbol and two decimal places.")]
static Task<string> FormatPriceAsync(
    [Description("The amount to format")] double amount,
    [Description("The currency code (e.g., USD, EUR, GBP)")] string currency,
    CancellationToken cancellationToken)
{
    string formatted = currency.ToUpperInvariant() switch
    {
        "USD" => $"${amount:N2} USD",
        "EUR" => $"{amount:N2} EUR",
        "GBP" => $"{amount:N2} GBP",
        "JPY" => $"{amount:N0} JPY",
        "CAD" => $"C${amount:N2} CAD",
        _ => $"{amount:N2} {currency}"
    };

    Console.WriteLine($"  [Tool] FormatPrice({amount:F2}, {currency}) = {formatted}");
    return Task.FromResult(formatted);
}

// ---------------------------------------------------------------
// Provider factory
// ---------------------------------------------------------------

/// <summary>Creates an IChatClient from configuration.</summary>
static IChatClient CreateChatClient(IConfiguration configuration)
{
    string provider = configuration["LlmProvider:Provider"]?.ToLowerInvariant() ?? "github";

    return provider switch
    {
        "azure" => new AzureOpenAIClient(
                new Uri(configuration["LlmProvider:AzureOpenAI:Endpoint"] ?? throw new InvalidOperationException("Missing endpoint")),
                new DefaultAzureCredential())
            .GetChatClient(configuration["LlmProvider:AzureOpenAI:Model"] ?? "gpt-4o-mini").AsIChatClient(),
        "openai" => new OpenAI.OpenAIClient(configuration["LlmProvider:OpenAI:ApiKey"] ?? throw new InvalidOperationException("Missing key"))
            .GetChatClient(configuration["LlmProvider:OpenAI:Model"] ?? "gpt-4o-mini").AsIChatClient(),
        "github" => new OpenAI.OpenAIClient(
                new ApiKeyCredential(configuration["LlmProvider:GitHub:Token"] ?? throw new InvalidOperationException("Missing token")),
                new OpenAI.OpenAIClientOptions { Endpoint = new Uri(configuration["LlmProvider:GitHub:Endpoint"] ?? "https://models.github.ai/inference") })
            .GetChatClient(configuration["LlmProvider:GitHub:Model"] ?? "openai/gpt-4o-mini").AsIChatClient(),
        "ollama" => new OllamaSharp.OllamaApiClient(
            new Uri(configuration["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"),
            configuration["LlmProvider:Ollama:Model"] ?? "llama3.2"),
        _ => throw new InvalidOperationException($"Unknown provider: '{provider}'")
    };
}
