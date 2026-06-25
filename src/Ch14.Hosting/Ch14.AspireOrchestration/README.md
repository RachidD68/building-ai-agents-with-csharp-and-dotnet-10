# Ch13.AspireOrchestration — .NET Aspire for Agent Orchestration

This project demonstrates using .NET Aspire to orchestrate multi-agent services
with built-in observability, health checks, and service discovery.

## Prerequisites

The Aspire AppHost requires the .NET Aspire workload or compatible tooling.
If you do not have it installed, you can install it with:

```bash
dotnet workload install aspire
```

If the Aspire workload is not available for your SDK version, the AppHost
project may not build. The ServiceDefaults project builds independently.

## Project Structure

```
Ch13.AspireOrchestration/
  ├── Ch13.AspireOrchestration.AppHost/       # Aspire orchestrator
  │   └── Program.cs                          # Registers Ch13.AspNetCoreHost
  └── Ch13.AspireOrchestration.ServiceDefaults/ # Shared service configuration
      └── Extensions.cs                        # OpenTelemetry + health checks
```

## What It Shows

- **AppHost** — The Aspire orchestrator that wires services together
- **ServiceDefaults** — Shared OpenTelemetry, health checks, and resilience
- **Project references** — How Aspire discovers and manages dependent services
- **Dashboard** — The Aspire dashboard for monitoring traces, logs, and metrics

## Running

```bash
cd Ch13.AspireOrchestration.AppHost
dotnet run
```

The Aspire dashboard will open automatically showing all registered services.

## Architecture

```
Aspire AppHost (orchestrator)
  └── agent-api (Ch13.AspNetCoreHost)
        ├── POST /chat       → assistant agent
        └── POST /summarize  → summarizer agent
```
