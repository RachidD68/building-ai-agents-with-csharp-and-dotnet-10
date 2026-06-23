# Ch14.OpenTelemetryAgent — Observability and Debugging

This project demonstrates the three pillars of observability applied to AI agents:
logging, metrics, and distributed tracing.

## What It Shows

### 1. Structured Logging
- `ILogger` integration for agent request/response logging
- Structured log properties for filtering and analysis

### 2. Custom Metrics
- **agent.tokens.input** — Input tokens consumed per request
- **agent.tokens.output** — Output tokens generated per request
- **agent.requests.total** — Total agent request count
- **agent.request.duration** — Request latency histogram (ms)
- **agent.tools.invocations** — Tool call counts
- **agent.errors.total** — Error counts by type

### 3. Distributed Tracing
- Custom `Activity` spans for each agent interaction
- Span attributes: agent name, prompt, token counts, duration
- Full trace context propagation across HTTP boundaries

## Running

```bash
dotnet user-secrets set "LlmProvider:GitHub:Token" "your-token"
dotnet run
```

The console exporter prints traces and metrics to stdout. In production,
replace the console exporter with Jaeger, Zipkin, Azure Monitor, or any
OTLP-compatible backend.

## Key Classes

| Class            | Purpose                                          |
|------------------|--------------------------------------------------|
| `AgentMetrics`   | Custom metrics and activity source for tracing   |
| `TraceFormatter` | Human-readable formatting of trace spans         |

## Production Exporters

Replace `AddConsoleExporter()` with:
- `AddOtlpExporter()` for OTLP (Jaeger, Grafana Tempo)
- `AddZipkinExporter()` for Zipkin
- `AddAzureMonitorTraceExporter()` for Azure Monitor
