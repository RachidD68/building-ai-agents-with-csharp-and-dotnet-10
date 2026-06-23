// ============================================================================
// Chapter 2 — Minimal API Demo
// Demonstrates the ASP.NET Core patterns used throughout the book: dependency
// injection (including KEYED services), middleware, structured logging with
// ILogger, the options pattern (IOptions<T>), and a thread-safe in-memory store.
// ============================================================================

using System.Collections.Concurrent;
using Ch02.MinimalApi.Services;
using Microsoft.Extensions.Options;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// --- Configuration and the options pattern ---
// Bind a POCO from configuration. Here we seed an in-memory default; in a real
// app the same "Weather" section would come from appsettings.json or User Secrets.
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Weather:DefaultCity"] = "Seattle",
});
builder.Services.Configure<WeatherOptions>(builder.Configuration.GetSection("Weather"));

// --- Dependency injection, including KEYED services ---
// Keyed registration is exactly how Chapter 13 registers named agents with
// AddAIAgent("assistant"); you resolve them with [FromKeyedServices("...")].
// CachedWeatherService is backed by a thread-safe ConcurrentDictionary because
// a Singleton serves every request concurrently.
builder.Services.AddKeyedSingleton<IWeatherService, LiveWeatherService>("live");
builder.Services.AddKeyedSingleton<IWeatherService, CachedWeatherService>("cached");

WebApplication app = builder.Build();

// --- Middleware: structured logging ---
// Never interpolate values into the message template — pass them as arguments
// so the logging pipeline can capture them as structured fields (Chapter 14).
app.Use(async (context, next) =>
{
    DateTime start = DateTime.UtcNow;
    await next(context);
    double elapsedMs = (DateTime.UtcNow - start).TotalMilliseconds;
    app.Logger.LogInformation(
        "{Method} {Path} -> {StatusCode} in {ElapsedMs:F0} ms",
        context.Request.Method, context.Request.Path, context.Response.StatusCode, elapsedMs);
});

// --- Endpoints ---

app.MapGet("/hello", () => new { Message = "Hello from Building AI Agents with C# and .NET 10!" });

app.MapPost("/echo", (EchoRequest request) => new EchoResponse(request.Text, request.Text.Length));

// Resolve a KEYED service via [FromKeyedServices], and inject ILogger + IOptions<T>.
app.MapGet("/weather/{city}", (
    string city,
    [FromKeyedServices("cached")] IWeatherService weather,
    IOptions<WeatherOptions> options,
    ILogger<Program> logger) =>
{
    string target = string.IsNullOrWhiteSpace(city) ? options.Value.DefaultCity : city;
    logger.LogInformation("Weather requested for {City}", target);   // structured template

    WeatherForecast? forecast = weather.GetForecast(target);
    return forecast is not null
        ? Results.Ok(forecast)
        : Results.NotFound(new { Error = $"No weather data for '{target}'." });
});

app.Run();

// ============================================================================
// Request/Response Models + Options
// ============================================================================

/// <summary>Request body for the echo endpoint.</summary>
internal sealed record EchoRequest(string Text);

/// <summary>Response from the echo endpoint.</summary>
internal sealed record EchoResponse(string Echo, int Length);

/// <summary>Weather forecast data returned by the weather endpoint.</summary>
internal sealed record WeatherForecast(string City, int TemperatureC, string Condition, int Humidity)
{
    /// <summary>Gets the temperature converted to Fahrenheit.</summary>
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}

/// <summary>Options bound from the "Weather" configuration section.</summary>
internal sealed class WeatherOptions
{
    public string DefaultCity { get; set; } = "Seattle";
}

// ============================================================================
// Services
// ============================================================================

namespace Ch02.MinimalApi.Services
{
    /// <summary>Abstraction for weather data retrieval.</summary>
    internal interface IWeatherService
    {
        /// <summary>Gets a forecast for the city, or null if not found.</summary>
        WeatherForecast? GetForecast(string city);
    }

    /// <summary>Simulated weather source with hardcoded data.</summary>
    internal sealed class LiveWeatherService : IWeatherService
    {
        private static readonly Dictionary<string, WeatherForecast> Forecasts = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Seattle"] = new("Seattle", 12, "Rainy", 85),
            ["London"] = new("London", 14, "Cloudy", 72),
            ["Tokyo"] = new("Tokyo", 22, "Sunny", 55),
            ["Nairobi"] = new("Nairobi", 25, "Partly Cloudy", 60),
            ["Sydney"] = new("Sydney", 19, "Clear", 48),
        };

        /// <inheritdoc />
        public WeatherForecast? GetForecast(string city) => Forecasts.GetValueOrDefault(city);
    }

    /// <summary>
    /// Wraps the "live" service with a thread-safe ConcurrentDictionary cache.
    /// Registered as a keyed Singleton, so one instance serves every concurrent
    /// request — which is exactly why the backing store must be thread-safe.
    /// </summary>
    internal sealed class CachedWeatherService(
        [FromKeyedServices("live")] IWeatherService inner) : IWeatherService
    {
        private readonly ConcurrentDictionary<string, WeatherForecast?> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <inheritdoc />
        public WeatherForecast? GetForecast(string city) =>
            _cache.GetOrAdd(city, inner.GetForecast);
    }
}
