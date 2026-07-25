// ============================================================================
// Chapter 8 — Tools and Function Calling (Database Lookup Tool with DI)
// Demonstrates tools backed by dependency-injected services.
// The tool class receives an IProductRepository via constructor injection.
//
// Key concepts:
//   - DI registration for tool dependencies
//   - AIFunctionFactory.Create with instance methods
//   - Multi-step agent workflow: search -> lookup -> order
// ============================================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;
using Ch08.DatabaseLookupTool;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

// ---------------------------------------------------------------
// Step 1: Register the product repository via DI
// ---------------------------------------------------------------
builder.Services.AddSingleton<IProductRepository, InMemoryProductRepository>();
builder.Services.AddSingleton<ProductTools>();

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();
ProductTools productTools = host.Services.GetRequiredService<ProductTools>();

Console.WriteLine("=== Chapter 8: Database Lookup Tool — DI-Backed Tools Demo ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ---------------------------------------------------------------
// Step 2: Create AIFunction wrappers for instance methods
// ---------------------------------------------------------------
// Unlike the CalculatorTool demo (static methods), here we pass
// instance methods from the DI-resolved ProductTools class.
AIFunction searchTool = AIFunctionFactory.Create(
    productTools.SearchProducts);
AIFunction lookupTool = AIFunctionFactory.Create(
    productTools.GetProductById);
AIFunction orderTool = AIFunctionFactory.Create(
    productTools.PlaceOrder);

Console.WriteLine("Registered DI-backed tools:");
Console.WriteLine($"  - {searchTool.Name}");
Console.WriteLine($"  - {lookupTool.Name}");
Console.WriteLine($"  - {orderTool.Name}\n");

ChatClientAgent agent = new(
    chatClient,
    instructions: "You are a helpful shopping assistant for Contoso. Help customers search for products, look up details, and place orders. Always confirm order details before placing them.",
    name: "ShoppingAssistant",
    description: "A shopping assistant that can search products, look up details, and place orders",
    tools: [searchTool, lookupTool, orderTool]);

// ---------------------------------------------------------------
// Step 3: Demonstrate multi-step workflows
// ---------------------------------------------------------------
string[] scenarios =
[
    "Show me all your electronics products.",
    "I need a good office setup. What furniture do you have and what would you recommend?",
    "I'd like to order 2 of the Ergo Keyboard V2. Look up the product and place the order right away — no need to confirm with me first.",
    "What accessories do you have under $50?"
];

foreach (string scenario in scenarios)
{
    AgentSession session = await agent.CreateSessionAsync(cts.Token);
    Console.WriteLine($"Customer: {scenario}");

    try
    {
        AgentResponse response = await agent.RunAsync(
            scenario,
            session,
            cancellationToken: cts.Token);

        Console.WriteLine($"Assistant: {response.Text}\n");
        Console.WriteLine(new string('-', 60));
        Console.WriteLine();
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n[Cancelled by user]");
        return;
    }
}

Console.WriteLine("=== Database Lookup Tool demo complete! ===");
