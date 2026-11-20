# Ch04.MinimalApiAgent

The policy agent behind two single-turn HTTP endpoints.

| Route | Reply |
|---|---|
| `POST /chat` `{ "message": "..." }` | 200 `{ agent, message, status, singleTurn }` with status `completed` or `truncated`; 400 `validation_error`; 502 `provider_error`; 504 `time_budget` |
| `GET /chat/stream?message=...` | server-sent events: `delta` events, then one `done` event `{ status, reason }` |
| `GET /` | a description of the routes |

- Single-turn: every request runs with no session, so no request sees another's history.
- Budgets (`AgentApiLimits`): message length (2000 characters), output tokens (400), time (60 s). A message over the size budget is refused before any model call.
- One retry owner: the endpoints do not retry; the HTTP resilience handler that `AddLlmChatClient` installs does, and the SDK's own retries are 0.
- A client that disconnects mid-stream cancels the run; the server logs it as `Cancelled`.

```bash
dotnet run --project src/Ch04.FirstAgent/Ch04.MinimalApiAgent                  # Ollama llama3.2
dotnet run --project src/Ch04.FirstAgent/Ch04.MinimalApiAgent -- --self-test   # fake model, loopback only
```

The self-test starts the real app on a loopback port with `FakeChatClient` in place of the provider, so it needs no model and no outside network.