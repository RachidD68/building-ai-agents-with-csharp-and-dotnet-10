# Ch19.OpenTelemetryAgent

The Contoso reimbursement assistant with OpenTelemetry on both layers, and an offline
self-test that asserts what Microsoft Agent Framework 1.28 and Microsoft.Extensions.AI 10.11
actually emit.

## What it wires

| Call | Emits |
|---|---|
| `ChatClientBuilder.UseOpenTelemetry(...)` | `chat <model>` spans, `gen_ai.client.token.usage` and `gen_ai.client.operation.duration` |
| `AIAgentBuilder.UseOpenTelemetry(...)` | `invoke_agent <name>(<id>)` and `execute_tool <tool>` spans |
| `WorkflowBuilder.WithOpenTelemetry()` | `workflow.build` and `invoke_workflow <name>` spans |
| `ContosoTelemetry` | `contoso.request` span; `contoso.assistant.*` metrics with bounded tags only |
| `StatusDescriptionRedactor` | drops exception messages from failed spans before any exporter |

Sources to subscribe to (`ContosoTelemetry.Sources`): `Contoso.Assistant`,
`Experimental.Microsoft.Agents.AI`, `Experimental.Microsoft.Extensions.AI`,
`Microsoft.Agents.AI.Workflows`. The `Experimental.` prefix means these names may change:
re-run the self-test after every package upgrade.

`EnableSensitiveData` is set to `false` in code on both layers. Left unset, it follows the
`OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT` environment variable.

Exporters come from the hosting chapter's `Ch17.AspireOrchestration.ServiceDefaults`: the console
exporter is always on, and OTLP is added when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (as the Aspire
AppHost does). Outside development, stdout is a telemetry store too: whatever collects it must meet
the same access rules as your traces, or drop the console exporter there. Azure Monitor
is used when `APPLICATIONINSIGHTS_CONNECTION_STRING` is set; that path is compiled but was not
run here.

## Running

```bash
dotnet run                  # one question to the configured model (Ollama by default), then the trace tree
dotnet run -- --self-test   # offline: scripted model, fake provider, in-memory exporters, RESULT n/n
```

The self-test covers the span names and attributes, the sources to subscribe to, sensitive data
(property and environment variable), cardinality (metric tags and span names), workflow spans
(`gen_ai.workflow.name`, not `workflow.name`) and retry owners (attempts multiply when the HTTP
pipeline, the SDK and the application all retry).
