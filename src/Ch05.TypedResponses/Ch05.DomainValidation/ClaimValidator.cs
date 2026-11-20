using System.Globalization;
using System.Text.RegularExpressions;

namespace Ch05.DomainValidation;

/// <summary>
/// The meaning checks that run after the shape check. Each rung of the ladder
/// produces a named state: presence gives Missing; currency, sign, line totals
/// and provenance give Invalid with one reason per broken rule. Nothing is
/// corrected, and the self-reported confidence is never read.
/// </summary>
public static partial class ClaimValidator
{
    public static ClaimOutcome Validate(ClaimDraft claim, string sourceText, ClaimRules rules)
    {
        // Rung 1: presence. An absent amount stays absent; it never becomes 0m.
        List<string> missing = [];
        if (claim.Amount is null) missing.Add("amount");
        if (string.IsNullOrWhiteSpace(claim.Currency)) missing.Add("currency");
        if (missing.Count > 0) return new Missing(missing);

        decimal amount = claim.Amount!.Value;
        string currency = claim.Currency!;
        List<string> reasons = [];

        // Rung 2: units. An ISO code in capitals, and the account's own currency.
        if (!IsoCode().IsMatch(currency))
            reasons.Add($"currency_not_iso: '{currency}'");
        else if (currency != rules.AccountCurrency)
            reasons.Add($"currency_mismatch: {currency}, the account is in {rules.AccountCurrency}");
        if (amount <= 0m)
            reasons.Add(Inv($"amount_not_positive: {amount}"));

        // Rung 3: cross-field consistency. Listed amounts must add up to the total.
        IReadOnlyList<ClaimLine?> lines = claim.Lines ?? [];
        if (lines.Any(l => l?.Amount is null))
            reasons.Add("line_amount_missing");
        else if (lines.Count > 0 && lines.Sum(l => l!.Amount!.Value) is var sum && sum != amount)
            reasons.Add(Inv($"lines_disagree_with_total: lines add up to {sum}, total is {amount}"));

        // Rung 4: provenance. The total must be a number the employee wrote.
        if (!SourceAmounts(sourceText).Contains(amount))
            reasons.Add(Inv($"amount_not_in_source: {amount}"));

        return reasons.Count == 0 ? new Valid(claim) : new Invalid(claim, reasons);
    }

    /// <summary>
    /// Every amount written in the text, read the same way under any culture:
    /// a comma or dot followed by one or two digits is the decimal separator,
    /// so "64,80" and "64.80" are both 64.80, never 6480.
    /// </summary>
    public static IReadOnlySet<decimal> SourceAmounts(string text)
    {
        HashSet<decimal> amounts = [];
        foreach (Match m in Number().Matches(text))
        {
            string token = m.Value;
            int last = token.LastIndexOfAny(['.', ',']);
            bool hasDecimals = last >= 0 && token.Length - last - 1 is 1 or 2;
            string digits = hasDecimals
                ? StripSeparators(token[..last]) + "." + token[(last + 1)..]
                : StripSeparators(token);
            if (decimal.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal value))
                amounts.Add(value);
        }
        return amounts;
    }

    private static string StripSeparators(string s) => s.Replace(".", "").Replace(",", "");

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    [GeneratedRegex(@"^[A-Z]{3}$")]
    private static partial Regex IsoCode();

    [GeneratedRegex(@"\d+(?:[.,]\d+)*")]
    private static partial Regex Number();
}
