# Exercise 02 — Receipt / Invoice to a Typed Record (Intermediate)

## Prompt

Extend the flagship `Ch11.ImageToTypedData`: read the invoice image and extract a
typed `Receipt` record with `RunAsync<T>()` and `ChatResponseFormat.ForJsonSchema<T>()`.
Then add a **reconciliation guard** — sum the line items yourself and report whether
the sum matches the model-extracted total (it won't match exactly here, because the
invoice total includes tax; that's the lesson).

## Key Concepts

- Multimodal input (image) + structured output (`RunAsync<T>`) — Chapter 7 + Chapter 11
- `ChatResponseFormat.ForJsonSchema<Receipt>()` to force a typed JSON shape
- Validating model output against an independent computation (don't trust a single number)

## Run

```
dotnet run
```

Configure a vision model under `VisionModel` in `appsettings.json` (Ollama `llava`,
or cloud `gpt-4o`). Local: `ollama pull llava`. A frontier model gives crisper numbers.
