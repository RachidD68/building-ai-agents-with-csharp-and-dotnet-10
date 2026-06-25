using System.Diagnostics;

namespace Ch15.OpenTelemetryAgent;

/// <summary>
/// Formats OpenTelemetry trace data into a human-readable tree view.
/// Useful for debugging and understanding the agent call chain.
/// </summary>
public static class TraceFormatter
{
    /// <summary>
    /// Formats a completed activity (span) as a human-readable string.
    /// </summary>
    /// <param name="activity">The completed activity to format.</param>
    /// <returns>A formatted string representation of the trace span.</returns>
    public static string FormatActivity(Activity activity)
    {
        TimeSpan duration = activity.Duration;
        string status = activity.Status switch
        {
            ActivityStatusCode.Ok => "OK",
            ActivityStatusCode.Error => "ERROR",
            _ => "UNSET"
        };

        // Indent based on parent span presence (simple heuristic)
        int depth = activity.Parent is not null ? 1 : 0;
        string indent = new(' ', depth * 2);

        string result = $"""
            {indent}+-- {activity.DisplayName}
            {indent}|   TraceId  : {activity.TraceId}
            {indent}|   SpanId   : {activity.SpanId}
            {indent}|   Status   : {status}
            {indent}|   Duration : {duration.TotalMilliseconds:F1} ms
            """;

        // Append any tags (attributes) set on the span
        IEnumerable<KeyValuePair<string, string?>> tags = activity.Tags;
        foreach (KeyValuePair<string, string?> tag in tags)
        {
            result += $"\n{indent}|   {tag.Key}: {tag.Value}";
        }

        return result;
    }

    /// <summary>
    /// Prints a summary banner for a completed observability demonstration.
    /// </summary>
    /// <param name="totalRequests">Total number of agent requests made.</param>
    /// <param name="totalInputTokens">Total input tokens consumed.</param>
    /// <param name="totalOutputTokens">Total output tokens generated.</param>
    /// <param name="totalDurationMs">Total elapsed time in milliseconds.</param>
    /// <param name="totalCostUsd">Total estimated cost in USD across all requests.</param>
    public static void PrintSummary(
        int totalRequests,
        long totalInputTokens,
        long totalOutputTokens,
        double totalDurationMs,
        double totalCostUsd)
    {
        Console.WriteLine();
        Console.WriteLine("============================================");
        Console.WriteLine("       Telemetry Summary");
        Console.WriteLine("============================================");
        Console.WriteLine($"  Total requests     : {totalRequests}");
        Console.WriteLine($"  Input tokens       : {totalInputTokens}");
        Console.WriteLine($"  Output tokens      : {totalOutputTokens}");
        Console.WriteLine($"  Total duration     : {totalDurationMs:F0} ms");
        Console.WriteLine($"  Avg latency        : {(totalRequests > 0 ? totalDurationMs / totalRequests : 0):F0} ms");
        Console.WriteLine($"  Estimated cost     : ${totalCostUsd:F6}");
        Console.WriteLine("============================================");
    }
}
