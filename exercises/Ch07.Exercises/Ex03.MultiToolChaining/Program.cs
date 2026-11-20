// ============================================================================
// Exercise 03: Multi-Tool Chaining (Challenge)
// ----------------------------------------------------------------------------
// Agent with five tools backed by a pretend e-commerce service: SearchProducts,
// GetProductDetails, CheckInventory, PlaceOrder, TrackOrder. The tools log
// every call with ILogger, so the chain is visible in the log. The invocation
// layer is built explicitly, as Ch07.LoopControl does, so the iteration cap is
// a number you chose, not the library default. PlaceOrder only changes an
// in-memory dictionary: it is a simulated write, made safe in the
// authorized-actions chapter.
//
//   dotnet run                    the three-step request, against the configured
//                                 model (Ollama llama3.2)
//   dotnet run -- --self-test     a scripted chat client, no network; RESULT n/n
// ============================================================================

using System.ComponentModel;
using System.Text.Json;
using AgenticAI.Common.Extensions;
using AgenticAI.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Contains("--self-test")) return await ChainSelfTest.RunAsync();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

// The tool class is DI-registered (a scale-up of Ch07.DatabaseLookupTool) so
// it can receive an ILogger via constructor injection.
builder.Services.AddSingleton<EcommerceTools>();

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

Console.WriteLine("=== Exercise: Multi-Tool Chaining ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

EcommerceTools ecommerceTools = host.Services.GetRequiredService<EcommerceTools>();
ChatClientAgent agent = ChainAgent.Create(chatClient, ecommerceTools);

// The three-step scenario: a single question that should produce four or
// five tool calls in the log.
const string scenario =
    "I want to order 2 blue widgets, check they're in stock at the warehouse in Seattle, " +
    "and track the shipment afterward.";

Console.WriteLine($"You: {scenario}\n");

try
{
    AgentSession session = await agent.CreateSessionAsync(cts.Token);
    AgentResponse response = await agent.RunAsync(scenario, session, cancellationToken: cts.Token);
    Console.WriteLine($"\nAgent: {response.Text}\n");
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    Console.WriteLine("\n[Cancelled]");
    return 0;
}

Console.WriteLine("=== Exercise complete! ===");
Console.WriteLine("Check the log lines above: the tool-call sequence is the point of this exercise.");
return 0;

// ---------------------------------------------------------------
// The agent, with an explicit invocation layer
// ---------------------------------------------------------------
internal static class ChainAgent
{
    /// <summary>
    /// Model round-trips allowed for one request. The chain needs four or five;
    /// eight leaves room for a retry and stops a model that never stops asking.
    /// </summary>
    public const int MaxIterations = 8;

    /// <summary>The agent the live run and the self-test share.</summary>
    public static ChatClientAgent Create(IChatClient innerClient, EcommerceTools tools)
    {
        IChatClient pipeline = innerClient
            .AsBuilder()
            .UseFunctionInvocation(configure: c => c.MaximumIterationsPerRequest = MaxIterations)
            .Build();

        // UseProvidedChatClientAsIs: the agent adds no function-invocation layer
        // of its own, so the cap above is the one that applies.
        return new ChatClientAgent(
            pipeline,
            new ChatClientAgentOptions
            {
                Name = "EcommerceAssistant",
                UseProvidedChatClientAsIs = true,
                ChatOptions = new ChatOptions
                {
                    Instructions = """
                        You are a helpful e-commerce assistant. Use the tools to search products,
                        look up details, check warehouse inventory, place orders, and track shipments.
                        Chain the tools as needed: find the product first, verify stock at the requested
                        warehouse, place the order, then track it. Summarize each step for the user.
                        """,
                    Tools =
                    [
                        AIFunctionFactory.Create(tools.SearchProducts),
                        AIFunctionFactory.Create(tools.GetProductDetails),
                        AIFunctionFactory.Create(tools.CheckInventory),
                        AIFunctionFactory.Create(tools.PlaceOrder),
                        AIFunctionFactory.Create(tools.TrackOrder),
                    ],
                },
            });
    }
}

// ---------------------------------------------------------------
// Offline proof: dotnet run -- --self-test
// ---------------------------------------------------------------
internal static class ChainSelfTest
{
    static int s_passed, s_total;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Multi-Tool Chaining (self-test, scripted model, no network) ===\n");

        // 1. The chain: the scripted model asks for one tool per turn, as a model would.
        ListLogger log = new();
        ScriptedToolChatClient chain = null!;
        chain = new(turn => turn.Results.Count switch
        {
            0 => chain.Call("SearchProducts", new Dictionary<string, object?> { ["query"] = "blue widget" }),
            1 => chain.Call("CheckInventory", new Dictionary<string, object?> { ["productId"] = 1, ["warehouse"] = "Seattle" }),
            2 => chain.Call("PlaceOrder", new Dictionary<string, object?> { ["productId"] = 1, ["quantity"] = 2, ["warehouse"] = "Seattle" }),
            3 => chain.Call("TrackOrder", new Dictionary<string, object?> { ["orderId"] = OrderIdFrom(turn.Results[2]) }),
            _ => ScriptedToolChatClient.Say("Ordered 2 Blue Widgets from Seattle; the shipment is in transit."),
        });
        AgentResponse done = await ChainAgent.Create(chain, new EcommerceTools(log)).RunAsync(
            "I want to order 2 blue widgets, check they're in stock at the warehouse in Seattle, and track the shipment afterward.");
        Console.WriteLine("-- the chain, from the ILogger lines --");
        foreach (string line in log.Lines) Console.WriteLine($"   {line}");
        Expect("the log shows the chain in order: search, inventory, order, track",
            log.Lines.Select(l => l.Split(' ')[2]).SequenceEqual(["SearchProducts", "CheckInventory", "PlaceOrder", "TrackOrder"]));
        Expect("each result fed the next call: TrackOrder found the order PlaceOrder created",
            chain.Turns[^1].Results[3].Result?.ToString()?.StartsWith("Order ORD-0001: 2 x Blue Widget") == true);
        Expect("the run ended with the model's text answer after five model calls",
            chain.Turns.Count == 5 && done.Text.StartsWith("Ordered 2 Blue Widgets"));

        // 2. The cap: a scripted model that never stops asking for CheckInventory.
        ListLogger runawayLog = new();
        ScriptedToolChatClient runaway = null!;
        runaway = new(turn => turn.ToolNames.Count == 0
            ? ScriptedToolChatClient.Say("I stopped: the tool budget for this request is spent.")
            : runaway.Call("CheckInventory", new Dictionary<string, object?> { ["productId"] = 1, ["warehouse"] = "Seattle" }));
        string? thrown = null;
        AgentResponse? capped = null;
        try { capped = await ChainAgent.Create(runaway, new EcommerceTools(runawayLog)).RunAsync("Keep checking Seattle stock."); }
        catch (Exception ex) { thrown = $"{ex.GetType().Name}: {ex.Message}"; }
        Console.WriteLine($"\n-- MaximumIterationsPerRequest = {ChainAgent.MaxIterations}, a model that never stops asking --");
        Console.WriteLine($"   CheckInventory ran {runawayLog.Lines.Count} time(s); the model was called {runaway.Turns.Count} time(s)");
        Console.WriteLine($"   the last request carried {runaway.Turns[^1].ToolNames.Count} tool(s)");
        Console.WriteLine($"   the run returned: {capped?.Text ?? thrown}");
        Expect($"the tool ran exactly {ChainAgent.MaxIterations} times, then the loop stopped invoking it",
            runawayLog.Lines.Count == ChainAgent.MaxIterations);
        Expect("the run stopped at the cap and returned the model's answer, with no exception",
            thrown is null && capped?.Text.StartsWith("I stopped") == true);

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    static string OrderIdFrom(FunctionResultContent placed) =>
        JsonDocument.Parse(placed.Result?.ToString() ?? "{}").RootElement.GetProperty("orderId").GetString() ?? "";

    static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}

/// <summary>An ILogger that keeps the formatted messages, so the self-test can read the chain.</summary>
internal sealed class ListLogger : ILogger<EcommerceTools>
{
    private readonly List<string> _lines = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<string> Lines { get { lock (_gate) return [.. _lines]; } }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_gate) _lines.Add(formatter(state, exception));
    }
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
