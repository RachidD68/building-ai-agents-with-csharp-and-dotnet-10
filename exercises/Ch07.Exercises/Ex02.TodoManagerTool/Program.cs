// ============================================================================
// Exercise 02: Todo Manager Tool (Intermediate)
// ----------------------------------------------------------------------------
// Stateful todo-list agent with three tools: AddTodo, ListTodos, CompleteTodo.
// The tools live in a TodoTools class that receives an in-memory TodoRepository
// via constructor injection, the DI pattern from Ch07.DatabaseLookupTool. The
// list is local state, not a business effect.
//
//   dotnet run                    a four-turn conversation on one session,
//                                 against the configured model (Ollama llama3.2)
//   dotnet run -- --self-test     direct method tests, no model; RESULT n/n
// ============================================================================

using System.ComponentModel;
using AgenticAI.Common.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Contains("--self-test")) return SelfTest();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

// ---------------------------------------------------------------
// Step 1: Register the repository and the tool class in DI.
// The repository stays a singleton so every tool call sees the same list.
// ---------------------------------------------------------------
builder.Services.AddSingleton<TodoRepository>();
builder.Services.AddSingleton<TodoTools>();

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

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
// Step 3: A multi-turn conversation on ONE session. The repository keeps
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
        AgentResponse response = await agent.RunAsync(turn, session, cancellationToken: cts.Token);
        Console.WriteLine($"Agent: {response.Text}\n");
    }
    catch (OperationCanceledException) when (cts.IsCancellationRequested)
    {
        Console.WriteLine("\n[Cancelled]");
        return 0;
    }
}

Console.WriteLine("=== Exercise complete! ===");
return 0;

// ---------------------------------------------------------------
// Offline proof: dotnet run -- --self-test
// The same DI registrations as the live run; the tool methods are called
// directly, in the order the conversation's turns would call them.
// ---------------------------------------------------------------
static int SelfTest()
{
    Console.WriteLine("=== Todo Manager Tool (self-test, no model) ===\n");
    int passed = 0, total = 0;
    void Expect(string name, bool ok) { total++; if (ok) passed++; Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}"); }

    ServiceCollection services = new();
    services.AddSingleton<TodoRepository>();
    services.AddSingleton<TodoTools>();
    using ServiceProvider provider = services.BuildServiceProvider();

    // Turn 1: add three items.
    TodoTools turn1 = provider.GetRequiredService<TodoTools>();
    string added = turn1.AddTodo("buy groceries", "high");
    turn1.AddTodo("write the chapter review", "medium");
    turn1.AddTodo("book a dentist appointment", "low");
    Expect("AddTodo returns the new item with its id and priority", added == "Added todo #1: buy groceries (priority: high)");

    // Turn 2: list, from a second resolution of the tool class.
    TodoTools turn2 = provider.GetRequiredService<TodoTools>();
    string listed = turn2.ListTodos();
    Expect("a later turn lists the items added earlier (same singleton repository)",
        ReferenceEquals(turn1, turn2) && listed.Split('\n').Length == 3 && listed.Contains("#2: [ ] write the chapter review"));

    // Turn 3: complete two, and one id that doesn't exist.
    string done1 = turn2.CompleteTodo(1);
    string done3 = turn2.CompleteTodo(3);
    string unknown = turn2.CompleteTodo(99);
    Console.WriteLine($"   CompleteTodo(99) -> {unknown}");
    Expect("CompleteTodo marks the right items", done1 == "Completed todo #1: buy groceries" && done3 == "Completed todo #3: book a dentist appointment");
    Expect("CompleteTodo on an unknown id returns a clear error string, no exception", unknown == "Todo #99 not found.");

    // Turn 4: list again.
    string after = provider.GetRequiredService<TodoTools>().ListTodos();
    Console.WriteLine("   ListTodos after the completions:");
    foreach (string line in after.Split('\n')) Console.WriteLine($"     {line}");
    Expect("the final list shows two items done and one still open",
        after.Contains("#1: [X]") && after.Contains("#2: [ ]") && after.Contains("#3: [X]"));

    Console.WriteLine($"\nRESULT {passed}/{total}");
    return passed == total ? 0 : 1;
}

// ---------------------------------------------------------------
// Repository: in-memory, registered as a DI singleton
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
// Tool class: receives the repository via constructor injection
// ---------------------------------------------------------------

/// <summary>
/// Tool class whose methods depend on an injected <see cref="TodoRepository"/>,
/// the same DI pattern Ch07.DatabaseLookupTool uses with IProductRepository.
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

/// <summary>A todo item.</summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="Title">The todo title.</param>
/// <param name="Priority">The priority: high, medium, or low.</param>
/// <param name="IsComplete">Whether the item is completed.</param>
record TodoItem(int Id, string Title, string Priority, bool IsComplete);
