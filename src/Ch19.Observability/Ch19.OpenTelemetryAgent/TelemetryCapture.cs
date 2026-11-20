using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Ch19.Observability;

/// <summary>One exported span, copied out of the OpenTelemetry pipeline.</summary>
public sealed record SpanRecord(
    string Source, string Name, string SpanId, string ParentSpanId, string TraceId,
    ActivityStatusCode Status, string? StatusDescription, IReadOnlyDictionary<string, string> Tags, IReadOnlyList<string> Events)
{
    public string? Tag(string key) => Tags.TryGetValue(key, out string? v) ? v : null;
}

/// <summary>One exported metric point: instrument name, its tags, and the value.</summary>
public sealed record MetricPointRecord(string Name, IReadOnlyDictionary<string, string> Tags, double Value);

/// <summary>
/// An in-memory exporter for tests. It sits where the OTLP or console exporter
/// would, behind the real SDK processor, so a span that reaches this list is a
/// span your backend would have received. (OpenTelemetry.Exporter.InMemory is
/// not pinned at 1.19.1 here, so this is the same idea in a few lines.)
/// </summary>
public sealed class SpanCapture : BaseExporter<Activity>
{
    private readonly List<SpanRecord> _spans = [];

    public IReadOnlyList<SpanRecord> Spans { get { lock (_spans) return [.. _spans]; } }

    public void Clear() { lock (_spans) _spans.Clear(); }

    public override ExportResult Export(in Batch<Activity> batch)
    {
        foreach (Activity a in batch)
        {
            Dictionary<string, string> tags = [];
            foreach (KeyValuePair<string, object?> t in a.TagObjects) tags[t.Key] = t.Value?.ToString() ?? "";
            List<string> events = [.. a.Events.Select(e =>
                e.Name + " " + string.Join(";", e.Tags.Select(t => $"{t.Key}={t.Value}")))];
            SpanRecord record = new(a.Source.Name, a.DisplayName, a.SpanId.ToHexString(),
                a.ParentSpanId == default ? "" : a.ParentSpanId.ToHexString(), a.TraceId.ToHexString(),
                a.Status, a.StatusDescription, tags, events);
            lock (_spans) _spans.Add(record);
        }
        return ExportResult.Success;
    }
}

/// <summary>The metric half: every point of every exported instrument, cumulative.</summary>
public sealed class MetricCapture : BaseExporter<Metric>
{
    private readonly List<MetricPointRecord> _points = [];

    public IReadOnlyList<MetricPointRecord> Points { get { lock (_points) return [.. _points]; } }

    public void Clear() { lock (_points) _points.Clear(); }

    public override ExportResult Export(in Batch<Metric> batch)
    {
        lock (_points)
        {
            _points.Clear();   // cumulative temporality: the latest export is the whole picture
            foreach (Metric metric in batch)
            {
                foreach (ref readonly MetricPoint point in metric.GetMetricPoints())
                {
                    Dictionary<string, string> tags = [];
                    foreach (KeyValuePair<string, object?> t in point.Tags) tags[t.Key] = t.Value?.ToString() ?? "";
                    double value = metric.MetricType switch
                    {
                        MetricType.LongSum or MetricType.LongSumNonMonotonic => point.GetSumLong(),
                        MetricType.DoubleSum or MetricType.DoubleSumNonMonotonic => point.GetSumDouble(),
                        MetricType.LongGauge => point.GetGaugeLastValueLong(),
                        MetricType.DoubleGauge => point.GetGaugeLastValueDouble(),
                        MetricType.Histogram => point.GetHistogramCount(),
                        _ => double.NaN,
                    };
                    _points.Add(new MetricPointRecord(metric.Name, tags, value));
                }
            }
        }
        return ExportResult.Success;
    }
}

/// <summary>Captures every log line (message, exception text, scope values) at every level.</summary>
public sealed class LogCapture : Microsoft.Extensions.Logging.ILoggerProvider, Microsoft.Extensions.Logging.ISupportExternalScope
{
    private readonly List<string> _lines = [];
    private Microsoft.Extensions.Logging.IExternalScopeProvider _scopes = new Microsoft.Extensions.Logging.LoggerExternalScopeProvider();

    public IReadOnlyList<string> Lines { get { lock (_lines) return [.. _lines]; } }

    public void Clear() { lock (_lines) _lines.Clear(); }

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void SetScopeProvider(Microsoft.Extensions.Logging.IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose() { }

    private sealed class Logger(LogCapture owner, string category) : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            List<string> scopes = [];
            owner._scopes.ForEachScope((scope, list) => list.Add(scope is IEnumerable<KeyValuePair<string, object?>> kv
                ? string.Join(",", kv.Select(p => $"{p.Key}={p.Value}"))
                : scope?.ToString() ?? ""), scopes);
            string line = $"{logLevel} {category}: {formatter(state, exception)} {exception} [{string.Join(" | ", scopes)}]";
            lock (owner._lines) owner._lines.Add(line);
        }
    }
}

/// <summary>Everything the telemetry pipeline let out, as text, for a leak search.</summary>
public static class TelemetryText
{
    public static IEnumerable<string> Of(IEnumerable<SpanRecord> spans, IEnumerable<MetricPointRecord> points,
        IEnumerable<string>? logs = null)
    {
        foreach (SpanRecord s in spans)
        {
            yield return s.Name;
            if (s.StatusDescription is { Length: > 0 } d) yield return d;
            foreach (KeyValuePair<string, string> t in s.Tags) yield return $"{t.Key}={t.Value}";
            foreach (string e in s.Events) yield return e;
        }
        foreach (MetricPointRecord p in points)
            foreach (KeyValuePair<string, string> t in p.Tags) yield return $"{p.Name} {t.Key}={t.Value}";
        foreach (string line in logs ?? []) yield return line;
    }
}
