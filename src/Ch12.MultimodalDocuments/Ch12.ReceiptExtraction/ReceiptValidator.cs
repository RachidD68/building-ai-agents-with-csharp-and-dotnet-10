using System.Globalization;
using System.Text.RegularExpressions;
using Ch08.ActionPolicy;

namespace Ch12.ReceiptExtraction;

/// <summary>
/// Validation AFTER extraction. The model reports what it read; these rules
/// decide what the application may use. No rule ever replaces a value: a
/// failed rule turns the field into ToVerify, an unreadable field stays
/// Unreadable with no value.
/// </summary>
public static partial class ReceiptValidator
{
    // An amount is digits with an optional two-digit decimal part. "64,8O",
    // "64.80 EUR" and "about 65" are not amounts; a person reads them instead.
    [GeneratedRegex(@"^\d{1,7}(\.\d{2})?$")]
    private static partial Regex AmountPattern();

    /// <param name="pages">One reading per page image, in page order; null when the reply was not a reading.</param>
    public static ReceiptProposal Validate(string documentId, IReadOnlyList<ReceiptReading?> pages, DateOnly today)
    {
        CheckedText vendor = Merge(pages, r => r.Vendor);
        CheckedText date = CheckDate(Merge(pages, r => r.Date), today);
        CheckedText currency = CheckCurrency(Merge(pages, r => r.Currency));
        CheckedAmount total = ToAmount(Merge(pages, r => r.Total));
        CheckedAmount charged = ToAmount(Merge(pages, r => r.AmountCharged));
        decimal? linesSum = SumLines(pages);

        if (total.State == FieldState.Present)
        {
            if (charged.State == FieldState.Present && charged.Value != total.Value)
                total = total with { State = FieldState.ToVerify, Reason = "conflicting_totals" };
            else if (linesSum is null)
                total = total with { State = FieldState.ToVerify, Reason = "lines_not_checkable" };
            else if (linesSum != total.Value)
                total = total with { State = FieldState.ToVerify, Reason = "lines_do_not_add_up" };
        }

        List<string> instructions = [.. pages.SelectMany(p => p?.PrintedInstructions ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))];
        return new ReceiptProposal(documentId, vendor, date, currency, total, linesSum, instructions);
    }

    /// <summary>
    /// One field across pages. The page number comes from the application (it
    /// sent one image per page), not from the model: that is provenance it can trust.
    /// </summary>
    private static CheckedText Merge(IReadOnlyList<ReceiptReading?> pages, Func<ReceiptReading, ReadField> field)
    {
        var present = new List<(int Page, string Text)>();
        int? unreadablePage = null;
        for (int i = 0; i < pages.Count; i++)
        {
            ReadField? f = pages[i] is { } reading ? field(reading) : null;
            string state = f?.State.Trim().ToLowerInvariant() ?? "unreadable";   // no reading: nothing was read
            if (state == "present" && !string.IsNullOrWhiteSpace(f!.Text)) present.Add((i + 1, f.Text.Trim()));
            else if (state != "absent") unreadablePage ??= i + 1;            // unknown states count as unreadable
        }
        if (present.Select(p => p.Text).Distinct().Count() > 1)
            return new(null, FieldState.ToVerify, present[0].Page, "conflicting_pages");
        if (present.Count > 0) return new(present[0].Text, FieldState.Present, present[0].Page);
        return unreadablePage is { } page
            ? new(null, FieldState.Unreadable, page)
            : new(null, FieldState.Absent, null);
    }

    private static CheckedAmount ToAmount(CheckedText t)
    {
        if (t.State != FieldState.Present) return new(null, t.State, t.Page, t.Reason);
        if (!AmountPattern().IsMatch(t.Value!)) return new(null, FieldState.ToVerify, t.Page, "not_a_number");
        decimal value = decimal.Parse(t.Value!, CultureInfo.InvariantCulture);
        return value > 0m
            ? new(value, FieldState.Present, t.Page)
            : new(value, FieldState.ToVerify, t.Page, "not_positive");
    }

    private static CheckedText CheckCurrency(CheckedText t) =>
        t.State != FieldState.Present ? t
        : CurrencyCode.Normalize(t.Value) is { } code ? t with { Value = code }
        : t with { State = FieldState.ToVerify, Reason = "not_a_currency_code" };

    private static CheckedText CheckDate(CheckedText t, DateOnly today)
    {
        if (t.State != FieldState.Present) return t;
        if (!DateOnly.TryParseExact(t.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d))
            return t with { State = FieldState.ToVerify, Reason = "not_a_date" };
        return d > today ? t with { State = FieldState.ToVerify, Reason = "date_in_future" } : t;
    }

    /// <summary>The sum of every line on every page, or null when any line amount is not a readable amount.</summary>
    private static decimal? SumLines(IReadOnlyList<ReceiptReading?> pages)
    {
        List<ReadLine> lines = [.. pages.SelectMany(p => p?.Lines ?? [])];
        if (lines.Count == 0 || pages.Any(p => p is null)) return null;
        decimal sum = 0m;
        foreach (ReadLine line in lines)
        {
            if (line.Amount.State.Trim().ToLowerInvariant() != "present" ||
                !AmountPattern().IsMatch(line.Amount.Text.Trim()))
                return null;
            sum += decimal.Parse(line.Amount.Text.Trim(), CultureInfo.InvariantCulture);
        }
        return sum;
    }
}

/// <summary>
/// The hand-off to the reimbursement policy. Only a proposal whose total,
/// currency and date are Present, and with no instruction printed on it,
/// becomes a ReimbursementProposal; everything else goes to a person first.
/// The employee comes from the caller's account, never from the receipt.
/// </summary>
public static class ReceiptHandoff
{
    public static ReimbursementProposal? ToProposal(ReceiptProposal receipt, Guid operationId, EmployeeAccount account) =>
        receipt.ReadyForPolicy
            ? new ReimbursementProposal(operationId, account.EmployeeId,
                receipt.Total.Value!.Value, receipt.Currency.Value!, account.Version)
            : null;
}
