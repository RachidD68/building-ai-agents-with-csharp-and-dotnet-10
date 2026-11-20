# Exercise 01 — Keyword Filter (Beginner)

## Prompt

Extend `Ch20.MiddlewarePipeline` with a middleware class that blocks requests based
on a configurable keyword list loaded from appsettings.json. Verify that when you
add "banana" to the list, a request mentioning "banana" is blocked, but other
requests pass.

## Key Concepts

- The blocklist lives in configuration (`"BlockedKeywords"` array in
  appsettings.json), not in code: `GetSection("BlockedKeywords").Get<string[]>()`
  reads it and the middleware takes the list
- `DelegatingChatClient` for custom middleware (`ContentFilterMiddleware` is the template)
- Short-circuiting the pipeline (blocking without calling the LLM) on both the
  blocking and streaming paths
- `ChatClientBuilder` / `AsBuilder().Use(...)` pipeline composition

## Accepted when

Adding "banana" to `BlockedKeywords` blocks only the requests that mention a
banana, and a test covers upper case and the look-alike variants: the text is
normalised (NFKC, format characters such as U+200B removed, common Cyrillic and
Greek look-alikes folded to Latin, lower case) before matching.

## Self-test

```bash
dotnet run -- --self-test   # offline, reads the keywords from appsettings.json: RESULT n/n
```
