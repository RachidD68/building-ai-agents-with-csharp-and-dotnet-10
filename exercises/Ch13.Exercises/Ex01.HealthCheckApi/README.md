# Exercise 01 — Health Check API (Beginner)

## Prompt

Web API hosting an agent with health check endpoint. GET /health returns agent status and metrics. POST /chat queries the agent.

## Key Concepts

- Agent hosting in ASP.NET Core
- Health check endpoint pattern
- Request metrics tracking with `Interlocked`
- DI registration of agents as singletons
