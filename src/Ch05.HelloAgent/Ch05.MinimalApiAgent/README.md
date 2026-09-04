# Ch05.MinimalApiAgent — Agent as a Web API

Exposes the pirate agent as an HTTP service using ASP.NET Core Minimal APIs.

## What You'll Learn

- Running agents inside ASP.NET Core
- Non-streaming JSON endpoint (`POST /chat`)
- Streaming Server-Sent Events endpoint (`GET /chat/stream`)
- Registering the agent as a singleton (state lives in the per-request session)
- Foreshadows keyed-DI multi-persona routing (Challenge exercise `Ex03.MultiPersonaApi`)

## Setup

```bash
cd src/Ch05.HelloAgent/Ch05.MinimalApiAgent
dotnet user-secrets set "LlmProvider:Provider" "openai"
dotnet user-secrets set "LlmProvider:OpenAI:ApiKey" "<your-token>"
```

## Run

```bash
dotnet run
# Then in another terminal:
curl -X POST http://localhost:5000/chat -H "Content-Type: application/json" -d '{"message":"What is LINQ?"}'
curl "http://localhost:5000/chat/stream?message=What+is+LINQ"
```
