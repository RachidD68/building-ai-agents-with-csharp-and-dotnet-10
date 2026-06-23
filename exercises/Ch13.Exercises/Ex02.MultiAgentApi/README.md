# Exercise 02 — Multi-Agent API (Intermediate)

## Prompt

Web API hosting 3 agents (translator, summarizer, analyzer) with keyed services. Each
has its own endpoint. One agent also exposes a streaming endpoint: `POST /summarize/stream`
returns the summary token by token via Server-Sent Events.

## Endpoints

| Method | Path                | Description                          |
|--------|---------------------|--------------------------------------|
| POST   | `/translate`        | Translate text to English           |
| POST   | `/summarize`        | Summarize text (JSON)               |
| POST   | `/summarize/stream` | Summarize text (SSE, token by token) |
| POST   | `/analyze`          | Sentiment analysis                  |
| GET    | `/agents`           | List registered agents              |

## Key Concepts

- Keyed DI services for multiple agents
- `[FromKeyedServices]` for endpoint resolution
- Multiple specialized agents in one API
- SSE streaming with `TypedResults.ServerSentEvents` + `RunStreamingAsync`
- Clean API design with record types

## Try the stream

```bash
curl -N -X POST http://localhost:5000/summarize/stream \
  -H "Content-Type: application/json" \
  -d '{"text":"<a few paragraphs to summarize>"}'
```
