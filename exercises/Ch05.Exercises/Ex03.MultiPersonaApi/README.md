# Exercise 03 — Multi-Persona API (Challenge)

## Prompt

Build a **Minimal API** with three POST endpoints, each backed by a different persona agent:

- `POST /chef` — Chef Gordon persona
- `POST /detective` — Detective Holmes persona
- `POST /poet` — Poet Luna persona
- `GET /personas` — List available personas

## Hints

- Use **keyed services** (`AddKeyedSingleton`) to register each agent.
- Use `[FromKeyedServices("key")]` to resolve the correct agent per endpoint.
- Each endpoint creates a fresh session for single-turn use.

## Expected Outcome

```bash
curl -X POST http://localhost:5000/chef -H "Content-Type: application/json" -d '{"message":"What is recursion?"}'
# {"persona":"ChefGordon","reply":"Ah, recursion is like a soufflé..."}
```

## Key Concepts

- Keyed DI services in .NET
- Multiple agents in a single API
- Minimal API with POST endpoints
- Agent persona design
