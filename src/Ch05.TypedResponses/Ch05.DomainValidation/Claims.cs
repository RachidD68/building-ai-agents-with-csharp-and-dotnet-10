using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ch05.DomainValidation;

/// <summary>
/// The minimal typed claim: the first record you would write. It deserializes
/// whenever the JSON has the right shape, and a missing amount key silently
/// becomes 0m. The self-test reproduces both.
/// </summary>
public sealed record ExpenseClaim(
    [property: Description("What the expense was for, e.g. Taxi")] string Description,
    [property: Description("Amount claimed, a number such as 64.80")] decimal Amount,
    [property: Description("ISO 4217 currency code, e.g. EUR")] string Currency,
    [property: Description("Date of the expense, yyyy-MM-dd")] DateOnly? Date);

/// <summary>
/// What the model is asked to return in the validated pipeline. Data to check,
/// not a fact: every field the text may not state is nullable, so "absent"
/// stays absent instead of becoming a default.
/// </summary>
public sealed record ClaimDraft
{
    [Description("What the expense was for, in a few words, e.g. Taxi")]
    public string? Description { get; init; }

    // Strict: a string such as "64,80" is a shape error, not a number to guess at.
    [Description("Total amount claimed as a JSON number with a dot, e.g. 64.80. Null if the text states no amount.")]
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public decimal? Amount { get; init; }

    [Description("ISO 4217 currency code in capitals, e.g. EUR. Null if the text states none.")]
    public string? Currency { get; init; }

    [Description("Date of the expense, yyyy-MM-dd. Null if the text states no date.")]
    public DateOnly? Date { get; init; }

    [Description("One entry per amount when the text lists several amounts; empty otherwise.")]
    public IReadOnlyList<ClaimLine> Lines { get; init; } = [];

    // Model output, not a calibrated probability. Printed, never used by a rule.
    [Description("Your own estimate of how sure you are, a number from 0.0 to 1.0.")]
    [JsonPropertyName("confidence")]
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public double? SelfReportedConfidence { get; init; }
}

/// <summary>One amount of a claim that lists several.</summary>
public sealed record ClaimLine(
    [property: Description("What this amount is for")] string? Description,
    [property: Description("The amount as a JSON number, e.g. 90.00")]
    [property: JsonNumberHandling(JsonNumberHandling.Strict)] decimal? Amount);

/// <summary>The account the claim is checked against.</summary>
public sealed record ClaimRules(string AccountCurrency);

/// <summary>Every way an extraction can end. No state is a default value.</summary>
public abstract record ClaimOutcome;

/// <summary>The claim passed the schema and every domain rule.</summary>
public sealed record Valid(ClaimDraft Claim) : ClaimOutcome;

/// <summary>The claim deserialized but breaks one or more rules, each named.</summary>
public sealed record Invalid(ClaimDraft Claim, IReadOnlyList<string> Reasons) : ClaimOutcome;

/// <summary>A required field is absent. Never filled in with a default.</summary>
public sealed record Missing(IReadOnlyList<string> Fields) : ClaimOutcome;

/// <summary>The provider's content filter stopped the reply.</summary>
public sealed record Refused(string? Raw) : ClaimOutcome;

/// <summary>The provider stopped the reply at its output limit.</summary>
public sealed record Truncated(string? Raw) : ClaimOutcome;

/// <summary>The reply could not be read as a claim, even after one retry.</summary>
public sealed record Unreadable(string? Raw, string Error) : ClaimOutcome;

public static class ClaimJson
{
    /// <summary>The serializer options the typed call and the self-test share.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerOptions.Web);
}
