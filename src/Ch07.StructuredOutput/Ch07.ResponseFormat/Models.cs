using System.Text.Json.Serialization;

namespace Ch07.ResponseFormat;

/// <summary>
/// A book summary used to demonstrate the typed <c>ResponseFormat</c> path
/// (<see cref="Microsoft.Extensions.AI.ChatResponseFormat.ForJsonSchema{T}"/>).
/// The same record shape could be driven through <c>RunAsync&lt;T&gt;()</c>; here
/// we set the schema on the run options instead and deserialize the raw text.
/// </summary>
public sealed record BookSummary
{
    /// <summary>The book title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    /// <summary>The author's full name.</summary>
    [JsonPropertyName("author")]
    public string Author { get; init; } = string.Empty;

    /// <summary>The year the book was first published.</summary>
    [JsonPropertyName("yearPublished")]
    public int YearPublished { get; init; }

    /// <summary>Two- or three-sentence plot summary.</summary>
    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    /// <summary>Primary themes explored in the book.</summary>
    [JsonPropertyName("themes")]
    public IReadOnlyList<string> Themes { get; init; } = [];
}

/// <summary>
/// A wrapper type that proves the documented workaround for the
/// <c>ResponseFormat</c> path: bare primitives and arrays are not supported,
/// so the list is wrapped in an object with a single property. With
/// <c>RunAsync&lt;T&gt;()</c> you could request <c>List&lt;string&gt;</c> directly,
/// but the schema-on-options path needs a top-level JSON object.
/// </summary>
public sealed record MovieListWrapper
{
    /// <summary>The recommended movie titles.</summary>
    [JsonPropertyName("movies")]
    public IReadOnlyList<string> Movies { get; init; } = [];
}
