# Exercise 02 — Response Validator (Intermediate)

## Prompt

Build a post-response validator middleware that checks the model's output against a
set of rules (no URLs, minimum length, maximum length, no unexpected code block in a
short/casual response). When any rule fails, replace the response with a safe
fallback message. Log the rule that triggered.

## Key Concepts

- Post-processing middleware: `base.GetResponseAsync(...)` first, then inspect
  `response.Text`
- Any violated rule routes to the safe fallback — replacement, never truncation
- Logging the violated rule(s), same shape as `ContentFilterMiddleware`
- Streaming path: buffer the stream, validate the full text, then emit either the
  buffered updates or the single fallback update
