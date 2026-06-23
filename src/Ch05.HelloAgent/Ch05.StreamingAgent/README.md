# Ch05.StreamingAgent — Streaming Responses

The same pirate agent, but using `RunStreamingAsync()` to display tokens as they arrive in real-time.

## What You'll Learn

- Streaming responses with `RunStreamingAsync()` and `IAsyncEnumerable<AgentResponseUpdate>`
- Time-to-first-token vs. total completion time
- When to choose streaming over non-streaming

## Setup

```bash
cd src/Ch05.HelloAgent/Ch05.StreamingAgent
dotnet user-secrets set "LlmProvider:Provider" "github"
dotnet user-secrets set "LlmProvider:GitHub:Token" "<your-token>"
```

## Run

```bash
dotnet run
```
