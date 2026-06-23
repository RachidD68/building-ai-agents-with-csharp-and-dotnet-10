# Exercise 03 — Full Safety Pipeline (Challenge)

## Prompt

Combine input sanitization + content filter + PII redaction + response length guard into a full safety pipeline. Show a message going through all layers.

## Key Concepts

- Multi-layer middleware pipeline
- `DelegatingChatClient` chaining with `AsBuilder().Use(...)`
- Input preprocessing (sanitize, filter, redact)
- Output postprocessing (length guard, truncation)
- Pipeline order matters: request flows outer-to-inner, response flows inner-to-outer
