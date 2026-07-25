// ============================================================================
// Exercise 02 — Todo Manager Tool (Intermediate)
// Stateful todo-list agent with three tools: AddTodo, ListTodos, CompleteTodo.
// The tools live in a TodoTools class that receives an in-memory TodoRepository
// via constructor injection — the DI pattern from Ch08.DatabaseLookupTool.
// FULLY STANDALONE.
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

// ---------------------------------------------------------------
// Step 1: Register the repository and the tool class in DI.
// The repository stays a singleton so every tool call sees the same list.
// ---------------------------------------------------------------
builder.Services.AddSingleton<TodoRepository>();
builder.Services.AddSingleton<TodoTools>();

using IHost host = builder.Build();
IConfiguration config = host.Services.GetRequiredService<IConfiguration>();
IChatClient chatClient = CreateChatClient(config);

Console.WriteLine("=== Exercise: Todo Manager Tool ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ---------------------------------------------------------------
// Step 2: Resolve the tool class from DI, then wrap its instance methods.
// ---------------------------------------------------------------
TodoTools todoTools = host.Services.GetRequiredService<TodoTools>();

AIFunction addTool = AIFunctionFactory.Create(todoTools.AddTodo);
AIFunction listTool = AIFunctionFactory.Create(todoTools.ListTodos);
AIFunction completeTool = AIFunctionFactory.Create(todoTools.CompleteTodo);

ChatClientAgent agent = new(
    chatClient,
    instructions: """
        You are a helpful todo list manager. Use the provided tools to manage the user's todos.
        When adding items, confirm what was added with its ID and priority.
        When listing, format the list nicely. When completing, confirm the action.
        """,
    name: "TodoManager",
    tools: [addTool, listTool, completeTool]);

// ---------------------------------------------------------------
// Step 3: A multi-turn conversation on ONE session — the repository keeps
// state across turns: add several items, list them, mark some complete.
// ---------------------------------------------------------------
AgentSession session = await agent.CreateSessionAsync(cts.Token);

string[] turns =
[
    "Add three todos: buy groceries with high priority, write the chapter review with medium priority, and book a dentist appointment with low priority.",
    "What's on my list right now?",
    "I bought the groceries and booked the dentist — mark both of those done.",
    "List everything again. What's still open?"
];

foreach (string turn in turns)
{
    Console.WriteLine($"You: {turn}");

    try
    {
        AgentResponse response = await agent.RunAsync(
            turn, session, cancellationToken: cts.Token);

        Console.WriteLine($"Agent: {response.Text}\n");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n[Cancelled]");
        return;
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
// Repository — in-memory, registered as a DI singleton
// ---------------------------------------------------------------

/// <summary>
/// A simple in-memory todo repository. Registered as a singleton in DI so the
/// same list backs every tool call for the lifetime of the app.
/// </summary>
sealed class TodoRepository
{
    private readonly List<TodoItem> _todos = [];
    private readonly Lock _lock = new();
    private int _nextId = 1;

    /// <summary>Adds a new todo and returns the created item.</summary>
    public TodoItem Add(string title, string priority)
    {
        lock (_lock)
        {
            TodoItem item = new(_nextId++, title, priority, IsComplete: false);
            _todos.Add(item);
            return item;
        }
    }

    /// <summary>Returns a snapshot of all todos.</summary>
    public IReadOnlyList<TodoItem> List()
    {
        lock (_lock)
        {
            return [.. _todos];
        }
    }

    /// <summary>Marks a todo complete by ID; returns the updated item or null.</summary>
    public TodoItem? Complete(int id)
    {
        lock (_lock)
        {
            int index = _todos.FindIndex(t => t.Id == id);
            if (index < 0)
            {
                return null;
            }

            _todos[index] = _todos[index] with { IsComplete = true };
            return _todos[index];
        }
    }
}

// ---------------------------------------------------------------
// Tool class — receives the repository via constructor injection
// ---------------------------------------------------------------

/// <summary>
/// Tool class whose methods depend on an injected <see cref="TodoRepository"/>,
/// the same DI pattern Ch08.DatabaseLookupTool uses with IProductRepository.
/// </summary>
sealed class TodoTools
{
    private readonly TodoRepository _repository;

    /// <summary>Initializes the tools with the injected repository.</summary>
    public TodoTools(TodoRepository repository)
    {
        _repository = repository;
    }

    /// <summary>Adds a new todo item.</summary>
    [Description("Adds a new todo item with a title and a priority. Returns the created item with its ID.")]
    public string AddTodo(
        [Description("The title/description of the todo item")] string title,
        [Description("The priority of the item: 'high', 'medium', or 'low'")] string priority)
    {
        TodoItem item = _repository.Add(title, priority);
        Console.WriteLine($"  [Tool] AddTodo: #{item.Id} \"{item.Title}\" ({item.Priority})");
        return $"Added todo #{item.Id}: {item.Title} (priority: {item.Priority})";
    }

    /// <summary>Lists all todo items.</summary>
    [Description("Lists all current todo items with their IDs, priorities, and completion status.")]
    public string ListTodos()
    {
        IReadOnlyList<TodoItem> todos = _repository.List();

        if (todos.Count == 0)
        {
            Console.WriteLine("  [Tool] ListTodos: (empty)");
            return "No todos found. The list is empty.";
        }

        Console.WriteLine($"  [Tool] ListTodos: {todos.Count} items");
        return string.Join("\n", todos.Select(t =>
            $"#{t.Id}: [{(t.IsComplete ? "X" : " ")}] {t.Title} (priority: {t.Priority})"));
    }

    /// <summary>Marks a todo item as completed.</summary>
    [Description("Marks a todo item as completed by its ID.")]
    public string CompleteTodo(
        [Description("The ID of the todo item to mark as complete")] int id)
    {
        TodoItem? item = _repository.Complete(id);

        if (item is null)
        {
            Console.WriteLine($"  [Tool] CompleteTodo: #{id} not found");
            return $"Todo #{id} not found.";
        }

        Console.WriteLine($"  [Tool] CompleteTodo: #{id} \"{item.Title}\"");
        return $"Completed todo #{id}: {item.Title}";
    }
}

// ---------------------------------------------------------------
// Data model
// ---------------------------------------------------------------

/// <summary>
/// A todo item.
/// </summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="Title">The todo title.</param>
/// <param name="Priority">The priority: high, medium, or low.</param>
/// <param name="IsComplete">Whether the item is completed.</param>
record TodoItem(int Id, string Title, string Priority, bool IsComplete);
