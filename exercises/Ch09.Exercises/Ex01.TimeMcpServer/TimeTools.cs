using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Ex01.TimeMcpServer.Server;

/// <summary>
/// MCP tools for time operations. The server exposes exactly one tool:
/// GetCurrentTime for a specified IANA timezone.
/// </summary>
[McpServerToolType]
public static class TimeTools
{
    /// <summary>
    /// Gets the current time in a specified timezone.
    /// </summary>
    /// <param name="timezone">
    /// The IANA timezone identifier (e.g., "America/New_York", "Europe/London", "Asia/Tokyo").
    /// Use "UTC" for Coordinated Universal Time.
    /// </param>
    /// <returns>The current time in the specified timezone.</returns>
    /// <exception cref="McpException">The id is not a known timezone.</exception>
    [McpServerTool]
    [Description("Gets the current date and time in a specified timezone. Use IANA timezone identifiers like 'America/New_York', 'Europe/London', 'Asia/Tokyo', or 'UTC'.")]
    public static string GetCurrentTime(
        [Description("IANA timezone identifier (e.g., 'America/New_York', 'UTC', 'Europe/London')")] string timezone = "UTC")
    {
        try
        {
            TimeZoneInfo tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            DateTimeOffset now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
            return $"Current time in {timezone}: {now:yyyy-MM-dd HH:mm:ss zzz} ({tz.DisplayName})";
        }
        catch (TimeZoneNotFoundException)
        {
            // A tool error (isError: true), not a normal result: the model must not
            // read it as a time, and must not guess one.
            throw new McpException($"Unknown timezone: '{timezone}'. Use IANA identifiers like 'America/New_York' or 'UTC'.");
        }
    }
}
