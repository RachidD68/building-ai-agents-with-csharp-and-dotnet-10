// ============================================================================
// Aspire Service Defaults
// Shared configuration for OpenTelemetry, health checks, and resilience.
// Each microservice in the Aspire graph calls AddServiceDefaults() to get
// consistent observability and health-check configuration.
// ============================================================================

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Ch17.AspireOrchestration.ServiceDefaults;

/// <summary>
/// Aspire-style service defaults for consistent observability across services.
/// </summary>
public static class Extensions
{
    /// <summary>
    /// Adds standard Aspire-style service defaults: OpenTelemetry, health checks.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The builder for chaining.</returns>
    public static IHostApplicationBuilder AddServiceDefaults(
        this IHostApplicationBuilder builder)
    {
        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();

        return builder;
    }

    /// <summary>
    /// Configures OpenTelemetry with tracing and metrics exporters.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The builder for chaining.</returns>
    public static IHostApplicationBuilder ConfigureOpenTelemetry(
        this IHostApplicationBuilder builder)
    {
        OpenTelemetryBuilder otel = builder.Services.AddOpenTelemetry();
        otel
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddConsoleExporter();
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddConsoleExporter();
            });

        // The Aspire dashboard receives telemetry over OTLP, not from the console.
        // Aspire passes its endpoint to every project it starts, so export there
        // when it is set; run on its own, the console exporter is all you get.
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }

        return builder;
    }

    /// <summary>
    /// Adds default health check endpoints for Aspire orchestration.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The builder for chaining.</returns>
    public static IHostApplicationBuilder AddDefaultHealthChecks(
        this IHostApplicationBuilder builder)
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(),
                tags: ["live"]);

        return builder;
    }
}
