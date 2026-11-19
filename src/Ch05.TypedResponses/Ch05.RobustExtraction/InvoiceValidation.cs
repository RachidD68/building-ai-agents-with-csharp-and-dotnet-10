using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Ch05.RobustExtraction;

/// <summary>
/// Soft validation for an <see cref="InvoiceExtraction"/>: a confidence check plus
/// cross-field invariants. Every rule can only add a warning; an extraction is
/// auto-processed only when no rule fired, so a high self-reported confidence
/// is never enough on its own.
/// </summary>
public static class InvoiceValidation
{
    /// <summary>The serializer options the agent run and the self-test share.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerOptions.Web)
    {
        WriteIndented = true
    };

    /// <summary>
    /// Cross-field and threshold checks. Returns human-readable warnings; the caller
    /// decides whether to route for review, log, or retry.
    /// </summary>
    public static IEnumerable<string> Validate(InvoiceExtraction inv)
    {
        if (ConfidenceCheck.Warning(inv.SelfReportedConfidence) is string confidenceWarning)
        {
            yield return confidenceWarning;
        }

        if (string.IsNullOrWhiteSpace(inv.Vendor))
        {
            yield return "No vendor identified — verify extraction.";
        }

        if (inv.AmountDue <= 0)
        {
            yield return "Amount due is zero or negative — verify extraction.";
        }

        if (inv.Priority == PaymentPriority.Urgent && inv.AmountDue == 0)
        {
            yield return "Urgent priority with no amount due — inconsistent.";
        }

        if (string.IsNullOrWhiteSpace(inv.Currency))
        {
            yield return "No currency identified — may need manual categorization.";
        }
    }

    /// <summary>Offline check (JSON parsing only, no LLM): dotnet run -- --self-test</summary>
    public static int SelfTest()
    {
        Console.WriteLine("=== Chapter 7: Robust Extraction — self-test (offline) ===\n");

        int passed = 0, total = 0;
        void Check(string name, bool ok)
        {
            total++; passed += ok ? 1 : 0;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        }

        const string Sound = """
            "vendor": "Northwind Cloud Services", "invoiceNumber": "INV-2026-0412",
            "amountDue": 4820.00, "currency": "USD", "priority": "Standard",
            "lineItems": ["Compute (320 hrs)"]
            """;

        InvoiceExtraction? Parse(string confidenceMember) =>
            JsonSerializer.Deserialize<InvoiceExtraction>($"{{ {Sound}{confidenceMember} }}", JsonOptions);

        bool Rejected(string confidenceMember)
        {
            try { Parse(confidenceMember); return false; }
            catch (JsonException) { return true; }
        }

        // Valid values: parsed, in range, and no confidence warning.
        InvoiceExtraction? valid = Parse(""", "confidence": 0.82""");
        Check("valid 0.82 parses and is in range",
            valid?.SelfReportedConfidence == 0.82 &&
            ConfidenceCheck.Assess(valid.SelfReportedConfidence) == ConfidenceStatus.Reported);
        Check("valid 0.82 on a sound extraction raises no warning",
            valid is not null && !Validate(valid).Any());
        Check("boundaries 0.0 and 1.0 are in range",
            ConfidenceCheck.Assess(Parse(""", "confidence": 0.0""")?.SelfReportedConfidence) == ConfidenceStatus.Low &&
            ConfidenceCheck.Assess(Parse(""", "confidence": 1.0""")?.SelfReportedConfidence) == ConfidenceStatus.Reported);
        Check("0.3 is flagged as low",
            ConfidenceCheck.Warning(Parse(""", "confidence": 0.3""")?.SelfReportedConfidence)?.StartsWith("Low") == true);

        // Out of range: parsed, but flagged and never trusted.
        foreach (string raw in new[] { "1.7", "-0.2", "95" })
        {
            InvoiceExtraction? outOfRange = Parse($""", "confidence": {raw}""");
            Check($"{raw} is out of range and flagged",
                outOfRange is not null &&
                ConfidenceCheck.Assess(outOfRange.SelfReportedConfidence) == ConfidenceStatus.OutOfRange &&
                Validate(outOfRange).Any(w => w.Contains("outside 0..1")));
        }

        // Missing or null: unknown, flagged.
        InvoiceExtraction? missing = Parse("");
        Check("missing field parses as null and is flagged",
            missing is not null && missing.SelfReportedConfidence is null &&
            Validate(missing).Any(w => w.Contains("missing")));
        Check("explicit null is flagged as missing",
            ConfidenceCheck.Assess(Parse(""", "confidence": null""")?.SelfReportedConfidence) == ConfidenceStatus.Missing);

        // NaN and strings: rejected, even though JsonSerializerOptions.Web reads
        // numbers from strings. [JsonNumberHandling(Strict)] on the property wins,
        // and a JsonException sends TryRunStructuredAsync down its retry path.
        Check("bare NaN literal is rejected by the parser", Rejected(""", "confidence": NaN"""));
        Check("string \"NaN\" is rejected by the parser", Rejected(""", "confidence": "NaN" """));
        Check("string \"0.9\" is rejected, not coerced to a number", Rejected(""", "confidence": "0.9" """));
        Check("string \"high\" is rejected by the parser", Rejected(""", "confidence": "high" """));
        Check("the Web options still read other numbers from strings (amountDue)",
            JsonSerializer.Deserialize<InvoiceExtraction>("""{ "amountDue": "12.50" }""", JsonOptions)?.AmountDue == 12.50m);

        // The schema the model is shown asks for a number, not a string.
        JsonElement schema = AIJsonUtilities.CreateJsonSchema(typeof(InvoiceExtraction), serializerOptions: JsonOptions);
        string confidenceSchema = schema.GetProperty("properties").GetProperty("confidence").GetRawText();
        Check("schema types confidence as a number, never a string",
            confidenceSchema.Contains("\"number\"") && !confidenceSchema.Contains("\"string\""));

        // NaN or infinity that reaches the record by another route is still flagged.
        Check("NaN set in code is flagged",
            Validate(valid! with { SelfReportedConfidence = double.NaN }).Any(w => w.Contains("not a number")));
        Check("infinity set in code is out of range",
            ConfidenceCheck.Assess(double.PositiveInfinity) == ConfidenceStatus.OutOfRange);

        // Never the sole gate: the book's case of 95% on a half-empty extraction.
        InvoiceExtraction overconfident = valid! with { SelfReportedConfidence = 0.95, Vendor = "", AmountDue = 0, Currency = "" };
        Check("0.95 confidence does not clear failed invariants",
            Validate(overconfident).Count() == 3);

        Console.WriteLine($"RESULT {passed}/{total}");
        return passed == total ? 0 : 1;
    }
}
