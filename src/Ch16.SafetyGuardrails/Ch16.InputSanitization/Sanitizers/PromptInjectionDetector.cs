using System.Text.RegularExpressions;

namespace Ch16.InputSanitization.Sanitizers;

/// <summary>
/// Detects common prompt injection patterns using regex-based heuristics.
/// Returns a detection result indicating whether the input is safe or suspicious.
/// </summary>
/// <remarks>
/// This is a heuristic approach suitable for demonstration and basic protection.
/// For production systems, consider combining with ML-based classifiers and
/// dedicated content safety APIs (e.g., Azure AI Content Safety).
/// </remarks>
public static partial class PromptInjectionDetector
{
    /// <summary>
    /// Result of a prompt injection detection scan.
    /// </summary>
    /// <param name="IsSafe">Whether the prompt passed all checks.</param>
    /// <param name="Reason">Description of why the prompt was flagged, if any.</param>
    /// <param name="MatchedPattern">The pattern that triggered the detection, if any.</param>
    public sealed record DetectionResult(
        bool IsSafe,
        string? Reason = null,
        string? MatchedPattern = null);

    /// <summary>
    /// Scans the input prompt for common injection patterns.
    /// </summary>
    /// <param name="prompt">The user's input prompt to scan.</param>
    /// <returns>A detection result indicating whether the prompt is safe.</returns>
    public static DetectionResult Scan(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new DetectionResult(true);
        }

        // Check each pattern against the input
        foreach ((Regex regex, string description) in InjectionPatterns)
        {
            Match match = regex.Match(prompt);
            if (match.Success)
            {
                return new DetectionResult(
                    IsSafe: false,
                    Reason: description,
                    MatchedPattern: match.Value);
            }
        }

        return new DetectionResult(true);
    }

    /// <summary>
    /// Known injection patterns and their descriptions.
    /// </summary>
    private static readonly (Regex Pattern, string Description)[] InjectionPatterns =
    [
        (IgnorePreviousRegex(), "Attempts to override system instructions"),
        (SystemPromptRegex(), "Attempts to inject system-level instructions"),
        (RolePlayRegex(), "Attempts to change the agent's role or persona"),
        (JailbreakRegex(), "Attempts to bypass safety restrictions"),
        (DataExfiltrationRegex(), "Attempts to extract system configuration"),
        (EncodedInjectionRegex(), "Contains suspicious encoded content patterns"),
    ];

    [GeneratedRegex(@"(?i)(ignore|forget|disregard|override)\s+(all\s+)?(previous|prior|above|earlier)\s+(instructions?|rules?|prompts?|context)", RegexOptions.Compiled)]
    private static partial Regex IgnorePreviousRegex();

    [GeneratedRegex(@"(?i)(system\s*:|<<\s*sys\s*>>|\[system\]|<\|system\|>)", RegexOptions.Compiled)]
    private static partial Regex SystemPromptRegex();

    [GeneratedRegex(@"(?i)(you\s+are\s+now|act\s+as\s+if|pretend\s+(to\s+be|you\s+are)|from\s+now\s+on\s+you)", RegexOptions.Compiled)]
    private static partial Regex RolePlayRegex();

    [GeneratedRegex(@"(?i)(jailbreak|do\s+anything\s+now|DAN\s+mode|developer\s+mode\s+enabled|no\s+restrictions)", RegexOptions.Compiled)]
    private static partial Regex JailbreakRegex();

    [GeneratedRegex(@"(?i)(reveal|show|print|output|display)\s+(your\s+)?(system\s+prompt|instructions|configuration|api\s+key|secret)", RegexOptions.Compiled)]
    private static partial Regex DataExfiltrationRegex();

    [GeneratedRegex(@"(?i)(base64|\\x[0-9a-f]{2}|&#\d+;|%[0-9a-f]{2}){3,}", RegexOptions.Compiled)]
    private static partial Regex EncodedInjectionRegex();
}
