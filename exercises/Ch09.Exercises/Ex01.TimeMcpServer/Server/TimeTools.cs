using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Ex01.TimeMcpServer.Server;

/// <summary>
/// MCP tools for time operations: getting current time and converting between timezones.
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
            return $"Unknown timezone: '{timezone}'. Use IANA identifiers like 'America/New_York' or 'UTC'.";
        }
    }

    /// <summary>
    /// Converts a time from one timezone to another.
    /// </summary>
    /// <param name="time">The time string in ISO 8601 format (e.g., "2026-03-16T14:30:00").</param>
    /// <param name="fromTimezone">The source IANA timezone identifier.</param>
    /// <param name="toTimezone">The target IANA timezone identifier.</param>
    /// <returns>The converted time string.</returns>
    [McpServerTool]
    [Description("Converts a specific time from one timezone to another. Time should be in ISO 8601 format.")]
    public static string ConvertTime(
        [Description("Time in ISO 8601 format (e.g., '2026-03-16T14:30:00')")] string time,
        [Description("Source IANA timezone (e.g., 'America/New_York')")] string fromTimezone,
        [Description("Target IANA timezone (e.g., 'Europe/London')")] string toTimezone)
    {
        try
        {
            if (!DateTime.TryParse(time, out DateTime parsed))
            {
                return $"Invalid time format: '{time}'. Use ISO 8601 format like '2026-03-16T14:30:00'.";
            }

            TimeZoneInfo fromTz = TimeZoneInfo.FindSystemTimeZoneById(fromTimezone);
            TimeZoneInfo toTz = TimeZoneInfo.FindSystemTimeZoneById(toTimezone);

            DateTimeOffset sourceTime = new(parsed, fromTz.GetUtcOffset(parsed));
            DateTimeOffset converted = TimeZoneInfo.ConvertTime(sourceTime, toTz);

            return $"{time} {fromTimezone} = {converted:yyyy-MM-dd HH:mm:ss zzz} {toTimezone}";
        }
        catch (TimeZoneNotFoundException ex)
        {
            return $"Unknown timezone: '{ex.Message}'. Use IANA identifiers.";
        }
    }
}
