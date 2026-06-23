# Ch07.ResponseFormat — The Other Door to Structured Output

Demonstrates `ResponseFormat`, the second way to get structured output. Where
`RunAsync<T>()` deserializes for you, `ResponseFormat` sets the schema on the run
options (or the agent) and hands back JSON *text* that you parse yourself.

Reach for `ResponseFormat` when the type is not known at compile time, the schema
is raw JSON, the format must be fixed at agent-creation time, you only need the
raw text, or you are wiring agents together (Chapters 11-12).

## What You'll Learn

- `ChatResponseFormat.Json` for schemaless JSON
- `ChatResponseFormat.ForJsonSchema<T>()` set on `ChatClientAgentRunOptions`
- `ChatResponseFormat.ForJsonSchema(JsonElement, name, description)` for raw schema strings
- The primitives/arrays workaround (`MovieListWrapper`) — the `ResponseFormat` path
  needs a top-level JSON object, so bare arrays must be wrapped
- Typed output while streaming: assemble updates with `ToAgentResponseAsync()`,
  *then* deserialize — there is no partial typed object mid-stream

## Run

```bash
dotnet run
```

## A note on the captured output

The committed output was produced against a local `llama3.2` via Ollama. Ollama
applies constrained decoding, so the *shape* is honored on every path. Semantic
completeness is a different matter: small local models sometimes leave an array
field (such as `themes`) empty even when the schema asks for it. That gap — shape
guaranteed, meaning not — is exactly the point the chapter makes. A frontier model
fills these in far more reliably.
