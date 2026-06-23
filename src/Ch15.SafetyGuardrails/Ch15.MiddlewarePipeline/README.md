# Ch15.MiddlewarePipeline — IChatClient Middleware Pipeline

This project demonstrates how to compose middleware around an `IChatClient`
using the `ChatClientBuilder` from Microsoft.Extensions.AI.

## What It Shows

- **DelegatingChatClient** — Base class for creating custom middleware
- **ChatClientBuilder** — Fluent API for composing middleware pipelines
- **Pipeline ordering** — Outermost middleware wraps innermost
- **Short-circuiting** — Content filter blocks without calling the LLM

## Middleware Components

| Middleware              | Purpose                                      |
|-------------------------|----------------------------------------------|
| `LoggingMiddleware`     | Logs every request and response              |
| `TimingMiddleware`      | Measures and logs request latency            |
| `ContentFilterMiddleware` | Blocks requests with prohibited keywords   |

## Pipeline Architecture

```
Request  →  ContentFilter  →  Timing  →  Logging  →  LLM
Response ←  ContentFilter  ←  Timing  ←  Logging  ←  LLM
```

## Running

```bash
dotnet user-secrets set "LlmProvider:GitHub:Token" "your-token"
dotnet run
```
