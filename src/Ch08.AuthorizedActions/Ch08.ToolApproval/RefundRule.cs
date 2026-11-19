using System.Globalization;
using System.Text.Json;
using Ch08.ActionPolicy;

namespace Ch08.ToolApproval;

/// <summary>
/// The auto-approval predicate for a refund amount, as the model wrote it.
/// Auto-approve only an amount that is more than zero and at most the policy's
/// low-value limit. The amount is read the same way on every machine
/// (invariant culture); anything that is not a plain number ("1,5", "abc",
/// null) escalates. It never approves on its own: the auto-approval rule also
/// asks ActionPolicy, which checks scope, currency and version.
/// </summary>
internal static class RefundRule
{
    public const decimal AutoApproveLimit = ReimbursementPolicy.LowValueLimit;

    public static bool ShouldAutoApprove(object? raw, out string shown)
    {
        if (!TryReadAmount(raw, out decimal amount))
        {
            shown = $"'{raw}' (unreadable amount)";
            return false;
        }
        shown = amount.ToString(CultureInfo.InvariantCulture);
        return amount > 0m && amount <= AutoApproveLimit;
    }

    public static bool TryReadAmount(object? raw, out decimal amount)
    {
        const NumberStyles plain = NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign;
        switch (raw)
        {
            case JsonElement { ValueKind: JsonValueKind.Number } n:
                return n.TryGetDecimal(out amount);
            case JsonElement { ValueKind: JsonValueKind.String } t:
                return decimal.TryParse(t.GetString(), plain, CultureInfo.InvariantCulture, out amount);
            case string text:
                return decimal.TryParse(text, plain, CultureInfo.InvariantCulture, out amount);
            case decimal d:
                amount = d; return true;
            case int or long or double or float:
                amount = Convert.ToDecimal(raw, CultureInfo.InvariantCulture); return true;
            default:
                amount = 0m; return false;
        }
    }

    /// <summary>The rule's 15 cases, run under de-DE on purpose ("1,5" would be 1.5 there).</summary>
    public static (int Passed, int Total) SelfTest()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            (object? Raw, bool Auto)[] cases =
            [
                (0.01m, true), (50m, true), (20, true),
                (JsonDocument.Parse("20").RootElement, true),
                (JsonDocument.Parse("\"1.5\"").RootElement, true),
                ("1.5", true),
                (-10m, false), (0m, false), (50.01m, false), (5000, false),
                (JsonDocument.Parse("-10").RootElement, false),
                ("1,5", false), ("abc", false), ("", false), (null, false),
            ];
            int passed = 0;
            foreach ((object? raw, bool expected) in cases)
            {
                bool actual = ShouldAutoApprove(raw, out string shown);
                bool ok = actual == expected;
                passed += ok ? 1 : 0;
                Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {shown,-28} -> {(actual ? "auto-approved" : "needs a decision")}");
            }
            return (passed, cases.Length);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
