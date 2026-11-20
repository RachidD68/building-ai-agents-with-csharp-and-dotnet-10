# Exercise 03: Multi-Persona API (Challenge)

## Prompt

Expose several agents behind one API. Register each persona's agent as a keyed service; the request names the persona.

```
POST /chat          { "persona": "pirate", "message": "What is recursion?" }
GET  /chat/stream   ?persona=pirate&message=...
GET  /personas
```

## Acceptance

- An unknown persona returns 404 (`unknown_persona`) with no model call.
- A validation error (400, `validation_error`) is distinct from a provider error (502, `provider_error`).
- The stream ends with an explicit `done` event carrying the terminal status.
- An offline test covers all three.

## Hints

- `AddKeyedSingleton(key, (sp, _) => new ChatClientAgent(...))` and `GetKeyedService<ChatClientAgent>(key)` for the dynamic choice.
- `TypedResults.ServerSentEvents` with `SseItem<string>`; `RunTracker` (from `Ch04.StreamingAgent`) tells you how the run ended.

## Running

```bash
dotnet run --project exercises/Ch04.Exercises/Ex03.MultiPersonaApi
dotnet run --project exercises/Ch04.Exercises/Ex03.MultiPersonaApi -- --self-test
```