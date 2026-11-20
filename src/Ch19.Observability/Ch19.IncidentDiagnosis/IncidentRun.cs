using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Ch19.Observability;

/// <summary>Everything one request exported: spans, metric points, log lines.</summary>
public sealed record IncidentTelemetry(string Answer, IReadOnlyList<SpanRecord> Spans,
    IReadOnlyList<MetricPointRecord> Points, IReadOnlyList<string> Logs);

/// <summary>Runs one request with an injected fault behind the real OpenTelemetry SDK.</summary>
public static class IncidentRun
{
    public const string Question = "Dana Whitfield took a client to dinner in Toronto. What is the limit for a client dinner?";

    public static async Task<IncidentTelemetry> RunAsync(Fault fault, string[]? sources = null, bool redact = true)
    {
        SpanCapture spans = new();
        MetricCapture metrics = new();
        LogCapture logs = new();
        TracerProviderBuilder tracing = Sdk.CreateTracerProviderBuilder().AddSource(sources ?? ContosoTelemetry.Sources);
        if (redact) tracing.AddProcessor(new StatusDescriptionRedactor());   // before the exporter
        using (TracerProvider tracer = tracing.AddProcessor(new SimpleActivityExportProcessor(spans)).Build()!)
        using (MeterProvider meter = Sdk.CreateMeterProviderBuilder()
                   .AddMeter(ContosoTelemetry.Meters)
                   .AddReader(new BaseExportingMetricReader(metrics)).Build()!)
        using (ILoggerFactory loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs)))
        using (ContosoTelemetry telemetry = new())
        {
            ReimbursementPipeline pipeline = new(telemetry, loggers, fault);
            string answer = await pipeline.AskAsync("conv-" + fault.ToString().ToLowerInvariant(), Question);
            tracer.ForceFlush();
            meter.ForceFlush();
            return new IncidentTelemetry(answer, spans.Spans, metrics.Points, logs.Lines);
        }
    }

    /// <summary>The framework sources only: what you have before adding step spans.</summary>
    public static readonly string[] FrameworkSourcesOnly =
        [.. ContosoTelemetry.Sources.Where(s => s != ContosoTelemetry.SourceName)];
}
