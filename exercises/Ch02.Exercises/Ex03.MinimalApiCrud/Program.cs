// ============================================================================
// Exercise 03 — Minimal API CRUD (Challenge)
// Full CRUD for a BookStore using Minimal APIs with an in-memory list.
// Endpoints: GET /books, GET /books/{id}, POST /books, PUT /books/{id},
//            DELETE /books/{id}
// ============================================================================

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Register the in-memory book store as a singleton
builder.Services.AddSingleton<BookStore>();

WebApplication app = builder.Build();

// ---------------------------------------------------------------
// Map CRUD endpoints
// ---------------------------------------------------------------

app.MapGet("/books", (BookStore store) =>
{
    return Results.Ok(store.GetAll());
})
.WithName("GetAllBooks")
.WithDescription("Returns all books in the store.");

app.MapGet("/books/{id:int}", (int id, BookStore store) =>
{
    Book? book = store.GetById(id);
    return book is not null ? Results.Ok(book) : Results.NotFound(new { error = $"Book {id} not found" });
})
.WithName("GetBookById")
.WithDescription("Returns a single book by its ID.");

app.MapPost("/books", (CreateBookRequest request, BookStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Author))
    {
        return Results.BadRequest(new { error = "Title and Author are required." });
    }

    Book created = store.Add(request.Title, request.Author, request.Isbn, request.Year);
    return Results.Created($"/books/{created.Id}", created);
})
.WithName("CreateBook")
.WithDescription("Adds a new book to the store.");

app.MapPut("/books/{id:int}", (int id, UpdateBookRequest request, BookStore store) =>
{
    Book? updated = store.Update(id, request.Title, request.Author, request.Isbn, request.Year);
    return updated is not null ? Results.Ok(updated) : Results.NotFound(new { error = $"Book {id} not found" });
})
.WithName("UpdateBook")
.WithDescription("Updates an existing book by its ID.");

app.MapDelete("/books/{id:int}", (int id, BookStore store) =>
{
    bool deleted = store.Delete(id);
    return deleted ? Results.NoContent() : Results.NotFound(new { error = $"Book {id} not found" });
})
.WithName("DeleteBook")
.WithDescription("Deletes a book by its ID.");

// Seed some initial data
BookStore seedStore = app.Services.GetRequiredService<BookStore>();
seedStore.Add("Clean Code", "Robert C. Martin", "978-0132350884", 2008);
seedStore.Add("The Pragmatic Programmer", "David Thomas & Andrew Hunt", "978-0135957059", 2019);
seedStore.Add("Design Patterns", "Gang of Four", "978-0201633610", 1994);

Console.WriteLine("=== Exercise: Minimal API CRUD — BookStore ===");
Console.WriteLine("Endpoints:");
Console.WriteLine("  GET    /books");
Console.WriteLine("  GET    /books/{id}");
Console.WriteLine("  POST   /books");
Console.WriteLine("  PUT    /books/{id}");
Console.WriteLine("  DELETE /books/{id}");
Console.WriteLine();

app.Run();

// ---------------------------------------------------------------
// Records and services
// ---------------------------------------------------------------

/// <summary>
/// Represents a book in the store.
/// </summary>
/// <param name="Id">The unique book identifier.</param>
/// <param name="Title">The book title.</param>
/// <param name="Author">The book author(s).</param>
/// <param name="Isbn">The ISBN number, if available.</param>
/// <param name="Year">The publication year, if available.</param>
record Book(int Id, string Title, string Author, string? Isbn, int? Year);

/// <summary>
/// Request payload for creating a new book.
/// </summary>
/// <param name="Title">The book title (required).</param>
/// <param name="Author">The book author (required).</param>
/// <param name="Isbn">The ISBN number (optional).</param>
/// <param name="Year">The publication year (optional).</param>
record CreateBookRequest(string Title, string Author, string? Isbn, int? Year);

/// <summary>
/// Request payload for updating an existing book.
/// </summary>
/// <param name="Title">Updated title (optional).</param>
/// <param name="Author">Updated author (optional).</param>
/// <param name="Isbn">Updated ISBN (optional).</param>
/// <param name="Year">Updated year (optional).</param>
record UpdateBookRequest(string? Title, string? Author, string? Isbn, int? Year);

/// <summary>
/// Thread-safe in-memory book store.
/// </summary>
sealed class BookStore
{
    private readonly Lock _lock = new();
    private readonly List<Book> _books = [];
    private int _nextId = 1;

    /// <summary>Returns all books.</summary>
    public List<Book> GetAll()
    {
        lock (_lock)
        {
            return [.. _books];
        }
    }

    /// <summary>Returns a book by ID, or null if not found.</summary>
    /// <param name="id">The book ID to look up.</param>
    public Book? GetById(int id)
    {
        lock (_lock)
        {
            return _books.FirstOrDefault(b => b.Id == id);
        }
    }

    /// <summary>Adds a new book and returns it with an assigned ID.</summary>
    /// <param name="title">The book title.</param>
    /// <param name="author">The book author.</param>
    /// <param name="isbn">The ISBN, if available.</param>
    /// <param name="year">The publication year, if available.</param>
    public Book Add(string title, string author, string? isbn, int? year)
    {
        lock (_lock)
        {
            Book book = new(_nextId++, title, author, isbn, year);
            _books.Add(book);
            return book;
        }
    }

    /// <summary>Updates a book by ID. Returns the updated book or null.</summary>
    /// <param name="id">The book ID to update.</param>
    /// <param name="title">New title, or null to keep existing.</param>
    /// <param name="author">New author, or null to keep existing.</param>
    /// <param name="isbn">New ISBN, or null to keep existing.</param>
    /// <param name="year">New year, or null to keep existing.</param>
    public Book? Update(int id, string? title, string? author, string? isbn, int? year)
    {
        lock (_lock)
        {
            int index = _books.FindIndex(b => b.Id == id);
            if (index < 0) return null;

            Book existing = _books[index];
            Book updated = existing with
            {
                Title = title ?? existing.Title,
                Author = author ?? existing.Author,
                Isbn = isbn ?? existing.Isbn,
                Year = year ?? existing.Year
            };
            _books[index] = updated;
            return updated;
        }
    }

    /// <summary>Deletes a book by ID. Returns true if deleted.</summary>
    /// <param name="id">The book ID to delete.</param>
    public bool Delete(int id)
    {
        lock (_lock)
        {
            int index = _books.FindIndex(b => b.Id == id);
            if (index < 0) return false;
            _books.RemoveAt(index);
            return true;
        }
    }
}
