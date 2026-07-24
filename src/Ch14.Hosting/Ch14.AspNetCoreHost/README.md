# Ch14.AspNetCoreHost — Hosting Multiple Agents in ASP.NET Core

This project demonstrates how to host multiple AI agents in a single ASP.NET Core
application using the Microsoft Agent Framework (MAF) Hosting package.

## What It Shows

- **AddAIAgent()** — Register agents in DI using the MAF Hosting builder pattern
- **WithInMemorySessionStore()** — Automatic per-agent session management
- **Keyed Services** — Resolve specific agents by name with `[FromKeyedServices("name")]`
- **Minimal API Endpoints** — Expose agents as HTTP services

## Endpoints

| Method | Path         | Description                    |
|--------|------------- |--------------------------------|
| POST   | `/chat`      | Chat with the assistant agent  |
| POST   | `/summarize` | Summarize text                 |
| GET    | `/agents`    | List registered agents         |
| GET    | `/`          | Service info                   |

## Running

```bash
# Set your API key via User Secrets
dotnet user-secrets set "LlmProvider:GitHub:Token" "your-token"

# Run the host
dotnet run
```

## Example Request

```bash
curl -X POST http://localhost:5000/chat \
  -H "Content-Type: application/json" \
  -d '{"message": "What is dependency injection in .NET?"}'
```

## Configuration

API keys are loaded from User Secrets (development) or environment variables
(production). See `appsettings.json` for the configuration schema and the
`LlmProvider` section for provider selection.

## Architecture

```
WebApplication
  ├── DI Container
  │   ├── IChatClient (singleton, from AgenticAI.Common)
  │   ├── AIAgent "assistant" (keyed singleton)
  │   └── AIAgent "summarizer" (keyed singleton)
  └── Minimal API
      ├── POST /chat       → resolves "assistant" agent
      └── POST /summarize  → resolves "summarizer" agent
```
