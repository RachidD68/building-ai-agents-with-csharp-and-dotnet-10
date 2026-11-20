# Ch17.AspireOrchestration — Aspire AppHost and ServiceDefaults

- **AppHost** starts `Ch17.AspNetCoreHost` as the `agent-api` resource, opens
  the Aspire dashboard, and marks the resource healthy when `/health/ready`
  answers 200 (`WithHttpHealthCheck`). Neither health endpoint calls the model.
- **ServiceDefaults** (`AddServiceDefaults`): OpenTelemetry tracing and metrics
  (console exporter, plus OTLP when Aspire sets `OTEL_EXPORTER_OTLP_ENDPOINT`)
  and the `self` liveness check. The observability chapter reuses it.

The Aspire version (13.6.5) is the one `Directory.Packages.props` pins for
`Aspire.Hosting`; the AppHost opts out of central package management because
the Aspire SDK adds its own references, so its `.csproj` repeats that version.
Change them together.

## Run

```bash
dotnet run --project src/Ch17.HostingIdentity/Ch17.AspireOrchestration/Ch17.AspireOrchestration.AppHost
```

The book's checks only build the AppHost: running it starts the dashboard and
the orchestration binaries the Aspire SDK restores for each operating system.
No Aspire workload is needed with Aspire 13 (the SDK is a NuGet package).
