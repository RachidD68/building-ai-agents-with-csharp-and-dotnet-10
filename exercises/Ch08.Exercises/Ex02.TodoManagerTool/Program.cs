// ============================================================================
// Exercise 02 — Todo Manager Tool (Intermediate)
// Agent with CRUD tools for a todo list. The agent manages the list through
// natural language. FULLY STANDALONE.
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

Console.WriteLine("=== Exercise: Todo Manager Tool ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Shared todo store
List<TodoItem> todoStore = [];
int nextId = 1;

// Tools run serially by default, but this guards against AllowConcurrentInvocation.
Lock storeLock = new();

// Register tools as closures over the shared store
AIFunction addTool = AIFunctionFactory.Create(
    [Description("Adds a new todo item and returns the created item with its ID")]
    (
        [Description("The title/description of the todo item")] string title,
        CancellationToken ct
    ) =>
    {
        TodoItem item;
        lock (storeLock)
        {
            item = new(nextId++, title, false);
            todoStore.Add(item);
        }
        Console.WriteLine($"  [Tool] AddTodo: #{item.Id} \"{item.Title}\"");
        return Task.FromResult($"Added todo #{item.Id}: {item.Title}");
    }, "AddTodo");

AIFunction listTool = AIFunctionFactory.Create(
    [Description("Lists all current todo items with their IDs and completion status")]
    (CancellationToken ct) =>
    {
        lock (storeLock)
        {
            if (todoStore.Count == 0)
            {
                Console.WriteLine("  [Tool] ListTodos: (empty)");
                return Task.FromResult("No todos found. The list is empty.");
            }

            string list = string.Join("\n", todoStore.Select(t =>
                $"#{t.Id}: [{(t.IsComplete ? "X" : " ")}] {t.Title}"));
            Console.WriteLine($"  [Tool] ListTodos: {todoStore.Count} items");
            return Task.FromResult(list);
        }
    }, "ListTodos");

AIFunction completeTool = AIFunctionFactory.Create(
    [Description("Marks a todo item as completed by its ID")]
    (
        [Description("The ID of the todo item to mark as complete")] int id,
        CancellationToken ct
    ) =>
    {
        TodoItem? item;
        lock (storeLock)
        {
            item = todoStore.FirstOrDefault(t => t.Id == id);
            if (item is null)
            {
                Console.WriteLine($"  [Tool] CompleteTodo: #{id} not found");
                return Task.FromResult($"Todo #{id} not found.");
            }

            int index = todoStore.IndexOf(item);
            todoStore[index] = item with { IsComplete = true };
        }
        Console.WriteLine($"  [Tool] CompleteTodo: #{id} \"{item.Title}\"");
        return Task.FromResult($"Completed todo #{id}: {item.Title}");
    }, "CompleteTodo");

AIFunction deleteTool = AIFunctionFactory.Create(
    [Description("Deletes a todo item by its ID")]
    (
        [Description("The ID of the todo item to delete")] int id,
        CancellationToken ct
    ) =>
    {
        TodoItem? item;
        lock (storeLock)
        {
            item = todoStore.FirstOrDefault(t => t.Id == id);
            if (item is null)
            {
                Console.WriteLine($"  [Tool] DeleteTodo: #{id} not found");
                return Task.FromResult($"Todo #{id} not found.");
            }

            todoStore.Remove(item);
        }
        Console.WriteLine($"  [Tool] DeleteTodo: #{id} \"{item.Title}\"");
        return Task.FromResult($"Deleted todo #{id}: {item.Title}");
    }, "DeleteTodo");

ChatClientAgent agent = new(
    chatClient,
    instructions: """
        You are a helpful todo list manager. Use the provided tools to manage the user's todos.
        When adding items, confirm what was added. When listing, format nicely.
        When completing or deleting, confirm the action.
        """,
    name: "TodoManager",
    tools: [addTool, listTool, completeTool, deleteTool]);

AgentSession session = await agent.CreateSessionAsync(cts.Token);

Console.WriteLine("Manage your todos through natural language (type 'quit' to exit):\n");

while (true)
{
    Console.Write("You: ");
    string? input = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(input) ||
        input.Equals("quit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    try
    {
        AgentResponse response = await agent.RunAsync(
            input, session, cancellationToken: cts.Token);

        Console.WriteLine($"Agent: {response.Text}\n");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n[Cancelled]");
        break;
    }
}

Console.WriteLine("=== Exercise complete! ===");

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
// Data model
// ---------------------------------------------------------------

/// <summary>
/// A todo item.
/// </summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="Title">The todo title.</param>
/// <param name="IsComplete">Whether the item is completed.</param>
record TodoItem(int Id, string Title, bool IsComplete);
