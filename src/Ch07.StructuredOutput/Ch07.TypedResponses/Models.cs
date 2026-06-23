using System.Text.Json.Serialization;

namespace Ch07.TypedResponses;

/// <summary>
/// A weather report with structured fields.
/// The agent will fill in all properties from natural language input.
/// </summary>
public sealed record WeatherReport
{
    /// <summary>The city or location name.</summary>
    [JsonPropertyName("location")]
    public string Location { get; init; } = string.Empty;

    /// <summary>Temperature in Celsius.</summary>
    [JsonPropertyName("temperatureCelsius")]
    public double TemperatureCelsius { get; init; }

    /// <summary>Weather conditions (e.g., "Sunny", "Cloudy", "Rainy").</summary>
    [JsonPropertyName("conditions")]
    public string Conditions { get; init; } = string.Empty;

    /// <summary>Humidity as a percentage (0-100).</summary>
    [JsonPropertyName("humidityPercent")]
    public int HumidityPercent { get; init; }

    /// <summary>Wind speed in km/h.</summary>
    [JsonPropertyName("windSpeedKmh")]
    public double WindSpeedKmh { get; init; }

    /// <summary>A brief human-readable summary.</summary>
    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;
}

/// <summary>
/// A movie review with ratings and structured analysis.
/// </summary>
public sealed record MovieReview
{
    /// <summary>The movie title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    /// <summary>Rating from 1 to 10.</summary>
    [JsonPropertyName("rating")]
    public int Rating { get; init; }

    /// <summary>Brief genre classification.</summary>
    [JsonPropertyName("genre")]
    public string Genre { get; init; } = string.Empty;

    /// <summary>Key strengths of the movie.</summary>
    [JsonPropertyName("strengths")]
    public IReadOnlyList<string> Strengths { get; init; } = [];

    /// <summary>Key weaknesses of the movie.</summary>
    [JsonPropertyName("weaknesses")]
    public IReadOnlyList<string> Weaknesses { get; init; } = [];

    /// <summary>One-sentence recommendation.</summary>
    [JsonPropertyName("recommendation")]
    public string Recommendation { get; init; } = string.Empty;
}

/// <summary>
/// A prioritized task list extracted from a natural language description.
/// </summary>
public sealed record TaskList
{
    /// <summary>The project or context name.</summary>
    [JsonPropertyName("projectName")]
    public string ProjectName { get; init; } = string.Empty;

    /// <summary>The individual tasks, ordered by priority.</summary>
    [JsonPropertyName("tasks")]
    public IReadOnlyList<TaskItem> Tasks { get; init; } = [];
}

/// <summary>
/// A single task within a task list.
/// </summary>
public sealed record TaskItem
{
    /// <summary>Short task title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    /// <summary>Detailed description of what needs to be done.</summary>
    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    /// <summary>Priority: "High", "Medium", or "Low".</summary>
    [JsonPropertyName("priority")]
    public string Priority { get; init; } = string.Empty;

    /// <summary>Estimated time to complete (e.g., "2 hours", "1 day").</summary>
    [JsonPropertyName("estimatedTime")]
    public string EstimatedTime { get; init; } = string.Empty;
}
