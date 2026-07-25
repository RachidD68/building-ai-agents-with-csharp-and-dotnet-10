# Exercise 01 — Keyword Filter (Beginner)

## Prompt

Extend `Ch16.MiddlewarePipeline` with a middleware class that blocks requests based
on a configurable keyword list loaded from appsettings.json. Verify that when you
add "banana" to the list, a request mentioning "banana" is blocked, but other
requests pass.

## Key Concepts

- The blocklist lives in configuration (`"BlockedKeywords"` array in
  appsettings.json), not in code — `IConfiguration` in the middleware constructor,
  `GetSection("BlockedKeywords").Get<string[]>()` to read it
- `DelegatingChatClient` for custom middleware (`ContentFilterMiddleware` is the template)
- Short-circuiting the pipeline (blocking without calling the LLM) on both the
  blocking and streaming paths
- `ChatClientBuilder` / `AsBuilder().Use(...)` pipeline composition
