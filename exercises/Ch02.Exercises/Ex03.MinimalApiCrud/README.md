# Exercise 03 — Minimal API CRUD (Challenge)

## Prompt

Build a **Minimal API** with full CRUD endpoints for a `TodoItem` record. Register an in-memory store as a **Singleton**. Add a **logging middleware** that writes each request's method, path, and duration to the console. Use the `IResult`-returning endpoint shape from `Ch02.MinimalApi`, and return appropriate `Results.Created`, `Results.Ok`, `Results.NoContent`, and `Results.NotFound` for each operation.

### Endpoints

| Method | Route          | Description             | Status Codes |
|--------|----------------|-------------------------|--------------|
| GET    | `/todos`       | List all todos          | 200          |
| GET    | `/todos/{id}`  | Get todo by ID          | 200, 404     |
| POST   | `/todos`       | Create a new todo       | 201, 400     |
| PUT    | `/todos/{id}`  | Update an existing todo | 200, 404     |
| DELETE | `/todos/{id}`  | Delete a todo           | 204, 404     |

## Hints

- The Minimal API companion project (`src/Ch02.CSharpEssentials/Ch02.MinimalApi`) has four of the five shapes you need. Start there, add the Update and Delete endpoints.
- Think carefully about what a POST should return when the resource doesn't exist yet — it *creates* the resource, so there is nothing to 404: return `Results.Created` with a Location header.
- The middleware is one `app.Use(...)` with a `Stopwatch` around `await next(context)`. Log method, path, status code, and elapsed milliseconds as structured fields, not interpolated strings.
- Register the store with `AddSingleton` and make it thread-safe (`Lock`) — one instance serves every concurrent request.
- Use `record ... with { ... }` for immutable updates.

## Expected Outcome

Run with `dotnet run`, then test with `curl` or a REST client:

```bash
curl http://localhost:5000/todos
curl http://localhost:5000/todos/1
curl -X POST http://localhost:5000/todos -H "Content-Type: application/json" -d '{"title":"New todo"}'
curl -X PUT http://localhost:5000/todos/1 -H "Content-Type: application/json" -d '{"isDone":true}'
curl -X DELETE http://localhost:5000/todos/1
```

Every request also produces a middleware log line in the console:

```
info: Ex03.MinimalApiCrud[0]
      GET /todos -> 200 in 12 ms
```

## Key Concepts

- Minimal API endpoint mapping with `IResult` return values
- Inline logging middleware (`app.Use`) with structured logging
- Dependency injection with `AddSingleton`
- Records with `with` expressions for immutable updates
- Proper HTTP status codes per CRUD operation
