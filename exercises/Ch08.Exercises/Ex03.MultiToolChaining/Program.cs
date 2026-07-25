// ============================================================================
// Exercise 03 — Multi-Tool Chaining (Challenge)
// Agent with five tools backed by a pretend e-commerce service: SearchProducts,
// GetProductDetails, CheckInventory, PlaceOrder, TrackOrder. The tools are
// instrumented with ILogger so the chaining sequence is visible in the log.
// FULLY STANDALONE.
// ============================================================================

using System.ClientModel;
using System.ComponentModel;
using System.Text.Json;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// The tool class is DI-registered (a scale-up of Ch08.DatabaseLookupTool) so
// it can receive an ILogger via constructor injection.
builder.Services.AddSingleton<EcommerceTools>();

using IHost host = builder.Build();
IConfiguration config = host.Services.GetRequiredService<IConfiguration>();
IChatClient chatClient = CreateChatClient(config);

Console.WriteLine("=== Exercise: Multi-Tool Chaining ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

EcommerceTools ecommerceTools = host.Services.GetRequiredService<EcommerceTools>();

AIFunction searchTool = AIFunctionFactory.Create(ecommerceTools.SearchProducts);
AIFunction detailsTool = AIFunctionFactory.Create(ecommerceTools.GetProductDetails);
AIFunction inventoryTool = AIFunctionFactory.Create(ecommerceTools.CheckInventory);
AIFunction orderTool = AIFunctionFactory.Create(ecommerceTools.PlaceOrder);
AIFunction trackTool = AIFunctionFactory.Create(ecommerceTools.TrackOrder);

ChatClientAgent agent = new(
    chatClient,
    instructions: """
        You are a helpful e-commerce assistant. Use the tools to search products,
        look up details, check warehouse inventory, place orders, and track shipments.
        Chain the tools as needed: find the product first, verify stock at the requested
        warehouse, place the order, then track it. Summarize each step for the user.
        """,
    name: "EcommerceAssistant",
    tools: [searchTool, detailsTool, inventoryTool, orderTool, trackTool]);

// The three-step scenario from the chapter brief — a single question that
// should produce four or five tool calls in the log.
const string scenario =
    "I want to order 2 blue widgets, check they're in stock at the warehouse in Seattle, " +
    "and track the shipment afterward.";

Console.WriteLine($"You: {scenario}\n");

try
{
    AgentSession session = await agent.CreateSessionAsync(cts.Token);
    AgentResponse response = await agent.RunAsync(
        scenario, session, cancellationToken: cts.Token);

    Console.WriteLine($"\nAgent: {response.Text}\n");
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled]");
    return;
}

Console.WriteLine("=== Exercise complete! ===");
Console.WriteLine("Check the log lines above — the tool-call sequence is the point of this exercise.");

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

// ---------------------------------------------------------------
// Pretend e-commerce service — five DI-backed tools with ILogger
// ---------------------------------------------------------------

/// <summary>
/// Tool class for a pretend e-commerce service. Five hardcoded products and
/// three warehouses — deliberately small; the ILogger instrumentation that
/// shows the chaining sequence is the point of the exercise.
/// </summary>
sealed class EcommerceTools
{
    private static readonly List<Product> Products =
    [
        new(1, "Blue Widget", "Widgets", 12.99m),
        new(2, "Red Widget", "Widgets", 12.99m),
        new(3, "Green Widget Deluxe", "Widgets", 19.99m),
        new(4, "Widget Carrying Case", "Accessories", 24.50m),
        new(5, "Widget Polish Kit", "Accessories", 8.75m),
    ];

    // Stock per (product ID, warehouse). Three warehouses are enough.
    private static readonly Dictionary<(int ProductId, string Warehouse), int> Stock = new()
    {
        [(1, "Seattle")] = 40, [(1, "Denver")] = 12, [(1, "Atlanta")] = 0,
        [(2, "Seattle")] = 25, [(2, "Denver")] = 0,  [(2, "Atlanta")] = 60,
        [(3, "Seattle")] = 5,  [(3, "Denver")] = 8,  [(3, "Atlanta")] = 3,
        [(4, "Seattle")] = 18, [(4, "Denver")] = 30, [(4, "Atlanta")] = 22,
        [(5, "Seattle")] = 50, [(5, "Denver")] = 45, [(5, "Atlanta")] = 15,
    };

    private static readonly string[] Warehouses = ["Seattle", "Denver", "Atlanta"];

    private readonly Dictionary<string, Order> _orders = [];
    private readonly Lock _lock = new();
    private readonly ILogger<EcommerceTools> _logger;
    private int _orderCounter;

    /// <summary>Initializes the tools with an injected logger.</summary>
    public EcommerceTools(ILogger<EcommerceTools> logger)
    {
        _logger = logger;
    }

    /// <summary>Searches the product catalog.</summary>
    [Description("Searches the product catalog by name or category. Returns matching products with their ID, name, category, and price.")]
    public string SearchProducts(
        [Description("The search term to match against product names and categories (e.g., 'blue widget', 'accessories')")] string query)
    {
        _logger.LogInformation("Tool call: SearchProducts query='{Query}'", query);

        List<Product> results = [.. Products.Where(p =>
            p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || p.Category.Contains(query, StringComparison.OrdinalIgnoreCase))];

        // Fall back to word-by-word matching so "2 blue widgets" still hits "Blue Widget".
        if (results.Count == 0)
        {
            string[] words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            results = [.. Products.Where(p => words.Any(w =>
                p.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
                || p.Category.Contains(w, StringComparison.OrdinalIgnoreCase)))];
        }

        return results.Count == 0
            ? $"No products found matching '{query}'."
            : JsonSerializer.Serialize(results, JsonSerializerOptions.Web);
    }

    /// <summary>Gets details for one product.</summary>
    [Description("Gets detailed information about a specific product by its ID: name, category, price, and which warehouses carry it.")]
    public string GetProductDetails(
        [Description("The product ID (use SearchProducts first to find it)")] int productId)
    {
        _logger.LogInformation("Tool call: GetProductDetails productId={ProductId}", productId);

        Product? product = Products.FirstOrDefault(p => p.Id == productId);
        if (product is null)
        {
            return $"No product found with ID {productId}.";
        }

        string[] carriedBy = [.. Warehouses.Where(w => Stock.GetValueOrDefault((productId, w)) > 0)];
        return JsonSerializer.Serialize(
            new { product.Id, product.Name, product.Category, product.Price, AvailableAtWarehouses = carriedBy },
            JsonSerializerOptions.Web);
    }

    /// <summary>Checks inventory at a warehouse.</summary>
    [Description("Checks how many units of a product are in stock at a specific warehouse. Warehouses: Seattle, Denver, Atlanta.")]
    public string CheckInventory(
        [Description("The product ID to check")] int productId,
        [Description("The warehouse name: 'Seattle', 'Denver', or 'Atlanta'")] string warehouse)
    {
        _logger.LogInformation("Tool call: CheckInventory productId={ProductId} warehouse={Warehouse}", productId, warehouse);

        string? matched = Warehouses.FirstOrDefault(w => w.Equals(warehouse.Trim(), StringComparison.OrdinalIgnoreCase));
        if (matched is null)
        {
            return $"Unknown warehouse '{warehouse}'. Valid warehouses: {string.Join(", ", Warehouses)}.";
        }

        Product? product = Products.FirstOrDefault(p => p.Id == productId);
        if (product is null)
        {
            return $"No product found with ID {productId}.";
        }

        int units = Stock.GetValueOrDefault((productId, matched));
        return units > 0
            ? $"{product.Name}: {units} units in stock at the {matched} warehouse."
            : $"{product.Name} is out of stock at the {matched} warehouse.";
    }

    /// <summary>Places an order from a warehouse.</summary>
    [Description("Places an order for a product from a specific warehouse. Check inventory first. Returns an order confirmation with the order ID for tracking.")]
    public string PlaceOrder(
        [Description("The product ID to order")] int productId,
        [Description("The number of units to order (positive integer)")] int quantity,
        [Description("The warehouse to ship from: 'Seattle', 'Denver', or 'Atlanta'")] string warehouse)
    {
        _logger.LogInformation("Tool call: PlaceOrder productId={ProductId} quantity={Quantity} warehouse={Warehouse}",
            productId, quantity, warehouse);

        string? matched = Warehouses.FirstOrDefault(w => w.Equals(warehouse.Trim(), StringComparison.OrdinalIgnoreCase));
        if (matched is null)
        {
            return $"Unknown warehouse '{warehouse}'. Valid warehouses: {string.Join(", ", Warehouses)}.";
        }

        Product? product = Products.FirstOrDefault(p => p.Id == productId);
        if (product is null)
        {
            return $"No product found with ID {productId}.";
        }

        lock (_lock)
        {
            int units = Stock.GetValueOrDefault((productId, matched));
            if (quantity > units)
            {
                return $"Insufficient stock for {product.Name} at {matched}: only {units} units available.";
            }

            Stock[(productId, matched)] = units - quantity;

            string orderId = $"ORD-{++_orderCounter:D4}";
            Order order = new(
                orderId, product.Name, quantity, matched,
                Status: "In transit",
                EstimatedDelivery: DateTime.UtcNow.AddDays(3).ToString("yyyy-MM-dd"),
                Total: product.Price * quantity);
            _orders[orderId] = order;

            return JsonSerializer.Serialize(order, JsonSerializerOptions.Web);
        }
    }

    /// <summary>Tracks an order by its ID.</summary>
    [Description("Tracks the shipment status of an order by its order ID (e.g., 'ORD-0001').")]
    public string TrackOrder(
        [Description("The order ID returned by PlaceOrder")] string orderId)
    {
        _logger.LogInformation("Tool call: TrackOrder orderId={OrderId}", orderId);

        lock (_lock)
        {
            if (!_orders.TryGetValue(orderId.Trim().ToUpperInvariant(), out Order? order))
            {
                return $"No order found with ID '{orderId}'.";
            }

            return $"Order {order.OrderId}: {order.Quantity} x {order.ProductName} — {order.Status} " +
                   $"from the {order.Warehouse} warehouse. Estimated delivery: {order.EstimatedDelivery}.";
        }
    }
}

// ---------------------------------------------------------------
// Data model
// ---------------------------------------------------------------

/// <summary>A product in the catalog.</summary>
/// <param name="Id">Unique product identifier.</param>
/// <param name="Name">Product name.</param>
/// <param name="Category">Product category.</param>
/// <param name="Price">Unit price in USD.</param>
sealed record Product(int Id, string Name, string Category, decimal Price);

/// <summary>A placed order.</summary>
/// <param name="OrderId">Unique order identifier (ORD-nnnn).</param>
/// <param name="ProductName">The ordered product.</param>
/// <param name="Quantity">Units ordered.</param>
/// <param name="Warehouse">The warehouse shipping the order.</param>
/// <param name="Status">Current shipment status.</param>
/// <param name="EstimatedDelivery">Estimated delivery date.</param>
/// <param name="Total">Total order price.</param>
sealed record Order(
    string OrderId,
    string ProductName,
    int Quantity,
    string Warehouse,
    string Status,
    string EstimatedDelivery,
    decimal Total);
