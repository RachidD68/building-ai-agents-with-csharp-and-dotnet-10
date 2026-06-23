using System.Text.Json.Serialization;

namespace Ch07.EntityExtraction;

/// <summary>
/// Sentiment classification for customer feedback.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<Sentiment>))]
public enum Sentiment
{
    /// <summary>The customer is happy or satisfied.</summary>
    Positive,

    /// <summary>The customer is neither positive nor negative.</summary>
    Neutral,

    /// <summary>The customer is unhappy or dissatisfied.</summary>
    Negative
}

/// <summary>
/// Urgency level for acting on customer feedback.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<Urgency>))]
public enum Urgency
{
    /// <summary>No immediate action required.</summary>
    Low,

    /// <summary>Should be addressed within a few days.</summary>
    Medium,

    /// <summary>Needs attention within 24 hours.</summary>
    High,

    /// <summary>Requires immediate action.</summary>
    Critical
}

/// <summary>
/// Structured extraction result from customer feedback text.
/// </summary>
public sealed record FeedbackAnalysis
{
    /// <summary>The customer's name if mentioned, otherwise "Unknown".</summary>
    [JsonPropertyName("customerName")]
    public string CustomerName { get; init; } = "Unknown";

    /// <summary>Overall sentiment of the feedback.</summary>
    [JsonPropertyName("sentiment")]
    public Sentiment Sentiment { get; init; }

    /// <summary>How urgently this feedback should be addressed.</summary>
    [JsonPropertyName("urgency")]
    public Urgency Urgency { get; init; }

    /// <summary>Main topic or category of the feedback.</summary>
    [JsonPropertyName("category")]
    public string Category { get; init; } = string.Empty;

    /// <summary>Product or service mentioned in the feedback.</summary>
    [JsonPropertyName("product")]
    public string Product { get; init; } = string.Empty;

    /// <summary>Key issues or complaints extracted from the text.</summary>
    [JsonPropertyName("issues")]
    public IReadOnlyList<string> Issues { get; init; } = [];

    /// <summary>Positive aspects mentioned by the customer.</summary>
    [JsonPropertyName("positiveAspects")]
    public IReadOnlyList<string> PositiveAspects { get; init; } = [];

    /// <summary>Suggested follow-up actions based on the feedback.</summary>
    [JsonPropertyName("suggestedActions")]
    public IReadOnlyList<string> SuggestedActions { get; init; } = [];

    /// <summary>One-sentence summary of the feedback.</summary>
    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    /// <summary>Confidence score from 0.0 to 1.0 for the extraction.</summary>
    [JsonPropertyName("confidence")]
    public double Confidence { get; init; }
}
