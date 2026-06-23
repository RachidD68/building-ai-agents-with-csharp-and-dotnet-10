# Exercise 03 — Minimal API CRUD (Challenge)

## Prompt

Build a **Minimal API** with full CRUD operations for a "BookStore" using an in-memory list.

### Endpoints

| Method | Route           | Description          | Status Codes     |
|--------|-----------------|----------------------|------------------|
| GET    | `/books`        | List all books       | 200              |
| GET    | `/books/{id}`   | Get book by ID       | 200, 404         |
| POST   | `/books`        | Create a new book    | 201, 400         |
| PUT    | `/books/{id}`   | Update existing book | 200, 404         |
| DELETE | `/books/{id}`   | Delete a book        | 204, 404         |

## Hints

- Use `<Project Sdk="Microsoft.NET.Sdk.Web">` for the web SDK.
- Define `Book` as an immutable `record`.
- Use `Results.Ok()`, `Results.Created()`, `Results.NotFound()`, `Results.NoContent()`, `Results.BadRequest()`.
- Register the store as a `Singleton` service.
- Use `record ... with { ... }` for immutable updates.

## Expected Outcome

Run with `dotnet run`, then test with `curl` or a REST client:

```bash
curl http://localhost:5000/books
curl http://localhost:5000/books/1
curl -X POST http://localhost:5000/books -H "Content-Type: application/json" -d '{"title":"New Book","author":"Author"}'
curl -X PUT http://localhost:5000/books/1 -H "Content-Type: application/json" -d '{"title":"Updated Title"}'
curl -X DELETE http://localhost:5000/books/1
```

## Key Concepts

- Minimal API endpoint mapping
- Dependency injection with `AddSingleton`
- Records with `with` expressions for immutable updates
- Proper HTTP status codes
- Thread-safe in-memory data store
