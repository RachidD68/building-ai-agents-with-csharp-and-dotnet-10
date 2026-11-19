using System.Globalization;

namespace Ch05.RobustExtraction;

/// <summary>How a model's self-reported confidence value should be treated.</summary>
public enum ConfidenceStatus
{
    /// <summary>The field was absent or null: confidence is unknown.</summary>
    Missing,

    /// <summary>The value is NaN: unusable.</summary>
    NotANumber,

    /// <summary>The value is outside 0.0..1.0 (including infinities): ignored.</summary>
    OutOfRange,

    /// <summary>A valid value below <see cref="ConfidenceCheck.ReviewThreshold"/>.</summary>
    Low,

    /// <summary>A valid value at or above the threshold. Still only the model's opinion.</summary>
    Reported
}

/// <summary>
/// Range and sanity checks for the model's <em>self-reported</em> confidence.
/// The number is the model's own opinion of its answer, not a calibrated
/// probability. A missing, NaN or out-of-range value is flagged and never
/// trusted, and even a valid high value never clears an extraction on its own:
/// confidence can add a review warning, it can never remove one.
/// </summary>
public static class ConfidenceCheck
{
    /// <summary>Lowest valid value (the record uses the 0.0..1.0 scale, not 0..100).</summary>
    public const double Min = 0.0;

    /// <summary>Highest valid value.</summary>
    public const double Max = 1.0;

    /// <summary>Below this the extraction is routed for manual review.</summary>
    public const double ReviewThreshold = 0.5;

    /// <summary>Classifies a raw self-reported value.</summary>
    public static ConfidenceStatus Assess(double? value) => value switch
    {
        null => ConfidenceStatus.Missing,
        double v when double.IsNaN(v) => ConfidenceStatus.NotANumber,
        double v when v < Min || v > Max => ConfidenceStatus.OutOfRange,
        double v when v < ReviewThreshold => ConfidenceStatus.Low,
        _ => ConfidenceStatus.Reported
    };

    /// <summary>Console text for the value, always labelled as self-reported.</summary>
    public static string Describe(double? value) => Assess(value) switch
    {
        ConfidenceStatus.Missing => "(missing) self-reported, not calibrated",
        ConfidenceStatus.NotANumber => "NaN, ignored (self-reported, not calibrated)",
        ConfidenceStatus.OutOfRange => $"{Raw(value)} is outside 0..1, ignored (self-reported, not calibrated)",
        _ => $"{value:P0} (self-reported, not calibrated)"
    };

    /// <summary>A review warning for the value, or null when it raises none.</summary>
    public static string? Warning(double? value) => Assess(value) switch
    {
        ConfidenceStatus.Missing => "Self-reported confidence missing — treat as unknown, manual review recommended.",
        ConfidenceStatus.NotANumber => "Self-reported confidence is not a number — ignored, manual review recommended.",
        ConfidenceStatus.OutOfRange => $"Self-reported confidence {Raw(value)} is outside 0..1 — ignored, manual review recommended.",
        ConfidenceStatus.Low => $"Low self-reported confidence ({value:P0}) — manual review recommended.",
        _ => null
    };

    private static string Raw(double? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "null";
}
