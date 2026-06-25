using System.Text.RegularExpressions;

namespace Ch16.InputSanitization.Sanitizers;

/// <summary>
/// Scans text for Personally Identifiable Information (PII) patterns.
/// Detects email addresses, phone numbers, credit card numbers, and SSNs.
/// </summary>
/// <remarks>
/// These regex patterns catch common formats but are not exhaustive.
/// For production, consider using Azure AI Language PII detection or
/// Microsoft Presidio for comprehensive PII identification.
/// </remarks>
public static partial class PiiDetector
{
    /// <summary>
    /// A single PII finding in the scanned text.
    /// </summary>
    /// <param name="Type">The type of PII found (e.g., "Email", "Phone").</param>
    /// <param name="Value">The matched text (redacted in output for safety).</param>
    /// <param name="Position">The character position where the PII was found.</param>
    public sealed record PiiFinding(string Type, string Value, int Position);

    /// <summary>
    /// Scans the given text for PII patterns and returns all findings.
    /// </summary>
    /// <param name="text">The text to scan for PII.</param>
    /// <returns>A list of PII findings. Empty if no PII detected.</returns>
    public static IReadOnlyList<PiiFinding> Scan(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        List<PiiFinding> findings = [];

        ScanPattern(findings, text, EmailRegex(), "Email");
        ScanPattern(findings, text, PhoneRegex(), "Phone");
        ScanPattern(findings, text, CreditCardRegex(), "CreditCard");
        ScanPattern(findings, text, SsnRegex(), "SSN");
        ScanPattern(findings, text, IpAddressRegex(), "IPAddress");

        return findings;
    }

    /// <summary>
    /// Redacts PII in the given text, replacing matches with a placeholder.
    /// </summary>
    /// <param name="text">The text containing PII to redact.</param>
    /// <returns>The text with PII replaced by [REDACTED] placeholders.</returns>
    public static string Redact(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        string result = text;
        result = EmailRegex().Replace(result, "[REDACTED-EMAIL]");
        result = PhoneRegex().Replace(result, "[REDACTED-PHONE]");
        result = CreditCardRegex().Replace(result, "[REDACTED-CC]");
        result = SsnRegex().Replace(result, "[REDACTED-SSN]");
        result = IpAddressRegex().Replace(result, "[REDACTED-IP]");

        return result;
    }

    private static void ScanPattern(
        List<PiiFinding> findings,
        string text,
        Regex pattern,
        string type)
    {
        foreach (Match match in pattern.Matches(text))
        {
            findings.Add(new PiiFinding(type, match.Value, match.Index));
        }
    }

    // Email: user@domain.tld
    [GeneratedRegex(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled)]
    private static partial Regex EmailRegex();

    // US phone: (xxx) xxx-xxxx, xxx-xxx-xxxx, +1xxxxxxxxxx
    [GeneratedRegex(@"(\+?1[-.\s]?)?(\(?\d{3}\)?[-.\s]?)?\d{3}[-.\s]?\d{4}\b", RegexOptions.Compiled)]
    private static partial Regex PhoneRegex();

    // Credit card: 16 digits with optional separators
    [GeneratedRegex(@"\b(?:\d{4}[-\s]?){3}\d{4}\b", RegexOptions.Compiled)]
    private static partial Regex CreditCardRegex();

    // SSN: xxx-xx-xxxx
    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.Compiled)]
    private static partial Regex SsnRegex();

    // IPv4 address
    [GeneratedRegex(@"\b(?:(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\.){3}(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\b", RegexOptions.Compiled)]
    private static partial Regex IpAddressRegex();
}
