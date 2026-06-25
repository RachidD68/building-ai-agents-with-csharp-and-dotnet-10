# Exercise 01 — Keyword Filter (Beginner)

## Prompt

Middleware that blocks messages containing a configurable list of banned words. Show blocked vs. allowed messages.

## Key Concepts

- `DelegatingChatClient` for custom middleware
- Short-circuiting the pipeline (blocking without calling LLM)
- `ChatClientBuilder` / `AsBuilder().Use(...)` pipeline composition
- Content safety through keyword matching
