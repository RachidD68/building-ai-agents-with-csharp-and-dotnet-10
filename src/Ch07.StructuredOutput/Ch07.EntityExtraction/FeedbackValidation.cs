using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ch07.EntityExtraction;

/// <summary>
/// Soft validation for a <see cref="FeedbackAnalysis"/>: a confidence check plus
/// cross-field invariants. Every rule can only add a warning; an extraction is
/// auto-processed only when no rule fired, so a high self-reported confidence
/// is never enough on its own.
/// </summary>
public static class FeedbackValidation
{
    /// <summary>The serializer options the agent run and the self-test share.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Returns human-readable warnings; empty means every check passed.</summary>
    public static List<string> Validate(FeedbackAnalysis analysis)
    {
        List<string> warnings = [];

        if (ConfidenceCheck.Warning(analysis.SelfReportedConfidence) is string confidenceWarning)
        {
            warnings.Add(confidenceWarning);
        }

        if (analysis.Urgency == Urgency.Critical && analysis.Sentiment != Sentiment.Negative)
        {
            warnings.Add("Critical urgency with non-negative sentiment — verify classification");
        }

        if (analysis.Issues.Count == 0 && analysis.Sentiment == Sentiment.Negative)
        {
            warnings.Add("Negative sentiment but no issues extracted — extraction may be incomplete");
        }

        if (string.IsNullOrWhiteSpace(analysis.Product))
        {
            warnings.Add("No product identified — may need manual categorization");
        }

        return warnings;
    }

    /// <summary>Offline check (JSON parsing only, no LLM): dotnet run -- --self-test</summary>
    public static int SelfTest()
    {
        Console.WriteLine("=== Chapter 7: Entity Extraction — self-test (offline) ===\n");

        int passed = 0, total = 0;
        void Check(string name, bool ok)
        {
            total++; passed += ok ? 1 : 0;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        }

        const string Sound = """
            "customerName": "Sarah Chen", "sentiment": "Negative", "urgency": "High",
            "category": "Sync", "product": "CloudSync Pro", "issues": ["uploads over 50MB fail"],
            "positiveAspects": [], "suggestedActions": ["Escalate to support"], "summary": "Sync fails."
            """;

        FeedbackAnalysis? Parse(string confidenceMember) =>
            JsonSerializer.Deserialize<FeedbackAnalysis>($"{{ {Sound}{confidenceMember} }}", JsonOptions);

        bool Rejected(string confidenceMember)
        {
            try { Parse(confidenceMember); return false; }
            catch (JsonException) { return true; }
        }

        // Valid values: parsed, in range, and no confidence warning.
        FeedbackAnalysis? valid = Parse(""", "confidence": 0.82""");
        Check("valid 0.82 parses and is in range",
            valid?.SelfReportedConfidence == 0.82 &&
            ConfidenceCheck.Assess(valid.SelfReportedConfidence) == ConfidenceStatus.Reported);
        Check("valid 0.82 on a sound extraction raises no warning",
            valid is not null && Validate(valid).Count == 0);
        Check("boundaries 0.0 and 1.0 are in range",
            ConfidenceCheck.Assess(Parse(""", "confidence": 0.0""")?.SelfReportedConfidence) == ConfidenceStatus.Low &&
            ConfidenceCheck.Assess(Parse(""", "confidence": 1.0""")?.SelfReportedConfidence) == ConfidenceStatus.Reported);
        Check("0.3 is flagged as low",
            ConfidenceCheck.Warning(Parse(""", "confidence": 0.3""")?.SelfReportedConfidence)?.StartsWith("Low") == true);

        // Out of range: parsed, but flagged and never trusted.
        foreach (string raw in new[] { "1.7", "-0.2", "95" })
        {
            FeedbackAnalysis? outOfRange = Parse($""", "confidence": {raw}""");
            Check($"{raw} is out of range and flagged",
                outOfRange is not null &&
                ConfidenceCheck.Assess(outOfRange.SelfReportedConfidence) == ConfidenceStatus.OutOfRange &&
                Validate(outOfRange).Any(w => w.Contains("outside 0..1")));
        }

        // Missing or null: unknown, flagged.
        FeedbackAnalysis? missing = Parse("");
        Check("missing field parses as null and is flagged",
            missing is not null && missing.SelfReportedConfidence is null &&
            Validate(missing).Any(w => w.Contains("missing")));
        Check("explicit null is flagged as missing",
            ConfidenceCheck.Assess(Parse(""", "confidence": null""")?.SelfReportedConfidence) == ConfidenceStatus.Missing);

        // NaN and strings: the parser rejects them instead of coercing.
        Check("bare NaN literal is rejected by the parser", Rejected(""", "confidence": NaN"""));
        Check("string \"NaN\" is rejected by the parser", Rejected(""", "confidence": "NaN" """));
        Check("string \"0.9\" is rejected, not coerced to a number", Rejected(""", "confidence": "0.9" """));
        Check("string \"high\" is rejected by the parser", Rejected(""", "confidence": "high" """));

        // NaN or infinity that reaches the record by another route is still flagged.
        Check("NaN set in code is flagged",
            Validate(valid! with { SelfReportedConfidence = double.NaN }).Any(w => w.Contains("not a number")));
        Check("infinity set in code is out of range",
            ConfidenceCheck.Assess(double.PositiveInfinity) == ConfidenceStatus.OutOfRange);

        // Never the sole gate: high confidence does not clear a broken extraction.
        FeedbackAnalysis overconfident = valid! with { SelfReportedConfidence = 0.99, Issues = [], Product = "" };
        Check("0.99 confidence does not clear failed invariants",
            Validate(overconfident).Count == 2);

        Console.WriteLine($"RESULT {passed}/{total}");
        return passed == total ? 0 : 1;
    }
}
