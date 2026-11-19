# Exercise 02 — Multi-Agent API (Intermediate)

## Prompt

Host four keyed agents in one API — a translator, a summarizer, a formalizer,
and a classifier — each at its own endpoint via `[FromKeyedServices]`, plus a
`/agents` discovery endpoint that lists them. One agent also exposes a
streaming endpoint: `POST /summarize/stream` returns the summary token by
token via `TypedResults.ServerSentEvents` over `RunStreamingAsync`.

## Endpoints

| Method | Path                | Description                              |
|--------|---------------------|------------------------------------------|
| POST   | `/translate`        | Translate text to English                |
| POST   | `/summarize`        | Summarize text (JSON)                    |
| POST   | `/summarize/stream` | Summarize text (SSE, token by token)     |
| POST   | `/formalize`        | Rewrite text in formal business tone     |
| POST   | `/classify`         | Classify text (sentiment + topic label)  |
| GET    | `/agents`           | List registered agents                   |

## Key Concepts

- Keyed DI services: one registration per agent (four in total)
- `[FromKeyedServices]` for endpoint resolution
- `/agents` discovery endpoint
- SSE streaming with `TypedResults.ServerSentEvents` + `RunStreamingAsync`
  (`[EnumeratorCancellation]` local iterator, `eventType: "token"`)
- Clean API design with record types

## Try the stream

```bash
curl -N -X POST http://localhost:5000/summarize/stream \
  -H "Content-Type: application/json" \
  -d '{"text":"<a few paragraphs to summarize>"}'
```
