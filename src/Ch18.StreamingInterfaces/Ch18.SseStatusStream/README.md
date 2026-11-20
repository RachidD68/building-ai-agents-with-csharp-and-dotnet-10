# Ch18.SseStatusStream: the operation's state is not the response's text

The interface chapter's GA path. One stream per conversation, with one meaning
per SSE event type:

| event | meaning |
|---|---|
| `delta` | a piece of the response's text |
| `tool` | tool status (`started`, `finished`) |
| `response.done` | the response stream ended; says nothing about the operation |
| `state` | the operation's state, set by the application after it observed the outcome |
| `cancel.requested` | the user asked; the result comes later as a `state` |
| `snapshot` | the whole view at a cursor (sent when the cursor is older than the window) |
| `resync` | the client's queue was full; it reconnects from its cursor |

States: Running, AwaitingApproval, Completed, Failed, Cancelled, Expired,
OutcomeUnknown. Only Completed, Failed, Cancelled and Expired are terminal;
OutcomeUnknown is not (reconciliation decides).

The envelope (`StatusEvent`) carries an event id, the conversation, the run,
the type, a schema version and the cursor. The SSE `id:` is the cursor, so a
browser's EventSource sends it back as `Last-Event-ID`.

- `OperationLog`: append-only per conversation, the last `retain` events
  kept for replay, a bounded queue per client (`Channel.CreateBounded`; a
  full queue drops the client, which gets `resync`).
- `StatusEndpoints.MapStatusStream`: `GET {prefix}/{id}/events` with
  `TypedResults.ServerSentEvents` and `SseItem<T>` (.NET 10), and
  `GET {prefix}/{id}/snapshot`. The host says who may read; anyone else gets 404.
- `StatusView`: the client's rules (an event id once, older cursors
  discarded, a skipped cursor means resync, another run's events ignored,
  "done" only for a terminal state). `NaiveView` is the first version, kept
  to reproduce the failures.
- `StatusStreamClient`: a C# reader built on `SseParser`.

## Run

```bash
dotnet run --project src/Ch18.StreamingInterfaces/Ch18.SseStatusStream -- --self-test
dotnet run --project src/Ch18.StreamingInterfaces/Ch18.SseStatusStream
```

The self-test (offline, scripted model, in-memory TestServer) reproduces each
failure with `NaiveView` first (DEFECT REPRODUCED), then shows the rule that
prevents it: response.done shown as done, a snapshot plus a replay doubling
the text, an older run's Completed ending the retry, an unbounded queue, a
Cancelled shown although the effect committed.

`dotnet run` is the live run: the model from `LlmProvider` (Ollama llama3.2
by default), one tool, an in-process server, and a client that drops the
connection after five events and resumes from its cursor. Simulated effects
only.
