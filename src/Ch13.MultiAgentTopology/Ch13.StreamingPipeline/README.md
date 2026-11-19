# Chapter 11 -- Streaming Pipeline

Demonstrates real pipeline UX: show **intermediate progress** while early stages run silently, then **stream the final stage's tokens** live. A three-agent chain (Outliner -> Drafter -> Polisher) runs the first two stages with blocking `RunAsync` (progress lines only) and streams the last stage with `RunStreamingAsync`.

## Key Concepts

| Concept | Description |
|---------|-------------|
| `RunAsync(...)` for intermediate stages | Their output is plumbing; the user sees only a progress line |
| `RunStreamingAsync(...)` for the final stage | Returns `IAsyncEnumerable<AgentResponseUpdate>`; each `update.Text` is written as it arrives |
| Manual chaining | Full control over what the user sees at each stage |

## Running

```bash
dotnet run --project src/Ch11.MultiAgent/Ch11.StreamingPipeline
```

Requires Ollama running locally with `llama3.2` (see `appsettings.json`).
