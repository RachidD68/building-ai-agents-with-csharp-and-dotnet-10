# Exercise 03 — Multi-Persona API (Challenge)

## Prompt

Extend `Ch05.MinimalApiAgent` so the `/chat` endpoint accepts a **persona field in the request body** ("pirate", "navigator", "helper"). Resolve a different `ChatClientAgent` per persona using keyed DI (`[FromKeyedServices]`). Add a `/personas` endpoint that returns the list of available personas **and their descriptions**.

## Hints

- `builder.Services.AddKeyedSingleton<ChatClientAgent>("pirate", (sp, _) => ...)` registers a keyed agent.
- In the endpoint, inject `[FromKeyedServices("pirate")] ChatClientAgent pirate` — or for dynamic selection, inject `IServiceProvider` and call `sp.GetRequiredKeyedService<ChatClientAgent>(key)`.
- The persona arrives per request, so this solution uses the dynamic route.

## Expected Outcome

```bash
curl -X POST http://localhost:5000/chat -H "Content-Type: application/json" \
  -d '{"persona":"pirate","message":"What is recursion?"}'
# {"persona":"pirate","agent":"CaptainCodeBeard","message":"Arrr, recursion be a function callin' itself..."}

curl http://localhost:5000/personas
# [{"persona":"pirate","name":"CaptainCodeBeard","description":"A pirate-themed AI assistant..."}, ...]
```

## Key Concepts

- Keyed DI services (`AddKeyedSingleton`, `GetRequiredKeyedService`)
- One endpoint, many agents — persona selected by the request body
- Discoverability via a `/personas` endpoint with descriptions
