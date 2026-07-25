// ============================================================================
// Exercise 03 — Minimal API CRUD (Challenge)
// Full CRUD for a TodoItem record using Minimal APIs: a singleton in-memory
// store and a logging middleware that writes each request's method, path,
// and duration to the console.
// Endpoints: GET /todos, GET /todos/{id}, POST /todos, PUT /todos/{id},
//            DELETE /todos/{id}
// ============================================================================

using System.Diagnostics;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Register the in-memory todo store as a Singleton — one instance serves
// every request concurrently, which is why the store must be thread-safe.
builder.Services.AddSingleton<TodoStore>();

WebApplication app = builder.Build();

// ---------------------------------------------------------------
// Logging middleware — method, path, status, and duration per request
// ---------------------------------------------------------------
// Same shape as the Ch02.MinimalApi companion: never interpolate values into
// the message template — pass them as arguments so the logging pipeline can
// capture them as structured fields (Chapter 14).
app.Use(async (context, next) =>
{
    Stopwatch stopwatch = Stopwatch.StartNew();
    await next(context);
    stopwatch.Stop();

    app.Logger.LogInformation(
        "{Method} {Path} -> {StatusCode} in {ElapsedMs:F0} ms",
        context.Request.Method, context.Request.Path,
        context.Response.StatusCode, stopwatch.Elapsed.TotalMilliseconds);
});

// ---------------------------------------------------------------
// CRUD endpoints — the IResult-returning shape from Ch02.MinimalApi
// ---------------------------------------------------------------

app.MapGet("/todos", (TodoStore store) =>
{
    return Results.Ok(store.GetAll());
})
.WithName("GetAllTodos")
.WithDescription("Returns all todo items.");

app.MapGet("/todos/{id:int}", (int id, TodoStore store) =>
{
    TodoItem? todo = store.GetById(id);
    return todo is not null ? Results.Ok(todo) : Results.NotFound(new { error = $"Todo {id} not found" });
})
.WithName("GetTodoById")
.WithDescription("Returns a single todo item by its ID.");

app.MapPost("/todos", (CreateTodoRequest request, TodoStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.Title))
    {
        return Results.BadRequest(new { error = "Title is required." });
    }

    // A POST creates the resource, so there is nothing to NotFound yet —
    // return 201 Created with a Location header pointing at the new todo.
    TodoItem created = store.Add(request.Title);
    return Results.Created($"/todos/{created.Id}", created);
})
.WithName("CreateTodo")
.WithDescription("Adds a new todo item.");

app.MapPut("/todos/{id:int}", (int id, UpdateTodoRequest request, TodoStore store) =>
{
    TodoItem? updated = store.Update(id, request.Title, request.IsDone);
    return updated is not null ? Results.Ok(updated) : Results.NotFound(new { error = $"Todo {id} not found" });
})
.WithName("UpdateTodo")
.WithDescription("Updates an existing todo item by its ID.");

app.MapDelete("/todos/{id:int}", (int id, TodoStore store) =>
{
    bool deleted = store.Delete(id);
    return deleted ? Results.NoContent() : Results.NotFound(new { error = $"Todo {id} not found" });
})
.WithName("DeleteTodo")
.WithDescription("Deletes a todo item by its ID.");

// Seed some initial data
TodoStore seedStore = app.Services.GetRequiredService<TodoStore>();
seedStore.Add("Read Chapter 2");
seedStore.Add("Run the Minimal API companion project");
seedStore.Add("Finish the practice exercises");

Console.WriteLine("=== Exercise: Minimal API CRUD — TodoItem ===");
Console.WriteLine("Endpoints:");
Console.WriteLine("  GET    /todos");
Console.WriteLine("  GET    /todos/{id}");
Console.WriteLine("  POST   /todos");
Console.WriteLine("  PUT    /todos/{id}");
Console.WriteLine("  DELETE /todos/{id}");
Console.WriteLine();

app.Run();

// ---------------------------------------------------------------
// Records and services
// ---------------------------------------------------------------

/// <summary>
/// Represents a single todo item.
/// </summary>
/// <param name="Id">The unique todo identifier.</param>
/// <param name="Title">What needs doing.</param>
/// <param name="IsDone">Whether the item is completed.</param>
record TodoItem(int Id, string Title, bool IsDone);

/// <summary>
/// Request payload for creating a new todo item.
/// </summary>
/// <param name="Title">The todo title (required).</param>
record CreateTodoRequest(string Title);

/// <summary>
/// Request payload for updating an existing todo item.
/// </summary>
/// <param name="Title">Updated title, or null to keep the existing one.</param>
/// <param name="IsDone">Updated completion state, or null to keep the existing one.</param>
record UpdateTodoRequest(string? Title, bool? IsDone);

/// <summary>
/// Thread-safe in-memory todo store.
/// </summary>
sealed class TodoStore
{
    private readonly Lock _lock = new();
    private readonly List<TodoItem> _todos = [];
    private int _nextId = 1;

    /// <summary>Returns all todo items.</summary>
    public List<TodoItem> GetAll()
    {
        lock (_lock)
        {
            return [.. _todos];
        }
    }

    /// <summary>Returns a todo by ID, or null if not found.</summary>
    /// <param name="id">The todo ID to look up.</param>
    public TodoItem? GetById(int id)
    {
        lock (_lock)
        {
            return _todos.FirstOrDefault(t => t.Id == id);
        }
    }

    /// <summary>Adds a new todo and returns it with an assigned ID.</summary>
    /// <param name="title">The todo title.</param>
    public TodoItem Add(string title)
    {
        lock (_lock)
        {
            TodoItem todo = new(_nextId++, title, IsDone: false);
            _todos.Add(todo);
            return todo;
        }
    }

    /// <summary>Updates a todo by ID. Returns the updated todo or null.</summary>
    /// <param name="id">The todo ID to update.</param>
    /// <param name="title">New title, or null to keep the existing one.</param>
    /// <param name="isDone">New completion state, or null to keep the existing one.</param>
    public TodoItem? Update(int id, string? title, bool? isDone)
    {
        lock (_lock)
        {
            int index = _todos.FindIndex(t => t.Id == id);
            if (index < 0) return null;

            TodoItem existing = _todos[index];
            TodoItem updated = existing with
            {
                Title = title ?? existing.Title,
                IsDone = isDone ?? existing.IsDone
            };
            _todos[index] = updated;
            return updated;
        }
    }

    /// <summary>Deletes a todo by ID. Returns true if deleted.</summary>
    /// <param name="id">The todo ID to delete.</param>
    public bool Delete(int id)
    {
        lock (_lock)
        {
            int index = _todos.FindIndex(t => t.Id == id);
            if (index < 0) return false;
            _todos.RemoveAt(index);
            return true;
        }
    }
}
