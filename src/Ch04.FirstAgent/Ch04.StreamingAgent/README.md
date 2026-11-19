# Ch04.StreamingAgent — Streaming Responses

The same pirate agent, but using `RunStreamingAsync()` to display tokens as they arrive in real-time.

## What You'll Learn

- Streaming responses with `RunStreamingAsync()` and `IAsyncEnumerable<AgentResponseUpdate>`
- Time-to-first-token vs. total completion time
- When to choose streaming over non-streaming

## Setup

```bash
cd src/Ch04.FirstAgent/Ch04.StreamingAgent
dotnet user-secrets set "LlmProvider:Provider" "openai"
dotnet user-secrets set "LlmProvider:OpenAI:ApiKey" "<your-token>"
```

## Run

```bash
dotnet run
```
