using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Ch07.RobustExtraction;

// ----------------------------------------------------------------------------
// Production-safe schema hints with [Description].
//
// XML <summary> comments only reach the generated schema when the project sets
// <GenerateDocumentationFile>true</GenerateDocumentationFile>. Turn that off and
// the hints vanish silently — no error, just a worse schema. [Description] from
// System.ComponentModel is honored by AIJsonUtilities regardless of build
// configuration, so it is the build-independent default for production models.
// ----------------------------------------------------------------------------

/// <summary>Priority an invoice should be paid at.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PaymentPriority>))]
public enum PaymentPriority
{
    [Description("Pay on the normal cycle; no rush.")]
    Standard,

    [Description("Pay ahead of the normal cycle to capture an early-payment discount.")]
    Expedited,

    [Description("Overdue or penalty-bearing; pay immediately.")]
    Urgent
}

/// <summary>
/// Structured data extracted from a free-text invoice or billing note.
/// Every member carries a [Description] so the schema hints survive even when
/// the documentation file is not generated.
/// </summary>
public sealed record InvoiceExtraction
{
    [Description("The vendor or supplier that issued the invoice.")]
    [JsonPropertyName("vendor")]
    public string Vendor { get; init; } = string.Empty;

    [Description("The invoice identifier or number, exactly as printed.")]
    [JsonPropertyName("invoiceNumber")]
    public string InvoiceNumber { get; init; } = string.Empty;

    [Description("The total amount due, in the invoice's currency, as a number.")]
    [JsonPropertyName("amountDue")]
    public decimal AmountDue { get; init; }

    [Description("The ISO 4217 currency code, e.g. USD, EUR, GBP.")]
    [JsonPropertyName("currency")]
    public string Currency { get; init; } = string.Empty;

    [Description("How urgently the invoice should be paid.")]
    [JsonPropertyName("priority")]
    public PaymentPriority Priority { get; init; }

    [Description("Line-item descriptions extracted from the invoice.")]
    [JsonPropertyName("lineItems")]
    public IReadOnlyList<string> LineItems { get; init; } = [];

    [Description("Extraction confidence from 0.0 to 1.0.")]
    [JsonPropertyName("confidence")]
    public double Confidence { get; init; }
}

// ----------------------------------------------------------------------------
// A small Result<T> discriminated union, consistent with the chapter's
// challenge exercise: a structured call either Succeeds with a value or Fails
// with messages and the raw text we could not parse.
// ----------------------------------------------------------------------------

/// <summary>The outcome of a structured extraction: either a value or a failure.</summary>
public abstract record Result<T>;

/// <summary>A successful extraction carrying the typed value.</summary>
public sealed record Success<T>(T Value) : Result<T>;

/// <summary>
/// A failed extraction. <paramref name="Messages"/> explains what went wrong;
/// <paramref name="RawResponse"/> preserves the model's text for offline review.
/// </summary>
public sealed record Failure<T>(IReadOnlyList<string> Messages, string? RawResponse) : Result<T>;
