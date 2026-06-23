using System.Text.Json.Serialization;

namespace Ch15.CriticAgent.Models;

/// <summary>
/// Structured result from the critic agent's evaluation of a response.
/// Returned via <c>RunAsync&lt;ValidationResult&gt;()</c> for type-safe access.
/// </summary>
public sealed record ValidationResult
{
    /// <summary>Whether the response passed validation.</summary>
    [JsonPropertyName("passed")]
    public bool Passed { get; init; }

    /// <summary>Overall quality score from 1 (poor) to 10 (excellent).</summary>
    [JsonPropertyName("score")]
    public int Score { get; init; }

    /// <summary>Assessment of factual accuracy and consistency.</summary>
    [JsonPropertyName("factualAssessment")]
    public string FactualAssessment { get; init; } = string.Empty;

    /// <summary>Assessment of tone and professionalism.</summary>
    [JsonPropertyName("toneAssessment")]
    public string ToneAssessment { get; init; } = string.Empty;

    /// <summary>Whether any PII was detected in the response.</summary>
    [JsonPropertyName("piiDetected")]
    public bool PiiDetected { get; init; }

    /// <summary>Specific issues found, if any.</summary>
    [JsonPropertyName("issues")]
    public IReadOnlyList<string> Issues { get; init; } = [];

    /// <summary>Suggestions for improvement, if any.</summary>
    [JsonPropertyName("suggestions")]
    public IReadOnlyList<string> Suggestions { get; init; } = [];
}
