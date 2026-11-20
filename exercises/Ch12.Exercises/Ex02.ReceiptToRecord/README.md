# Exercise 02 — Receipt / Invoice to a Typed Record (Intermediate)

## Prompt

Extend `Ch12.ImageToTypedData`: read the invoice image into a typed `Receipt` record with `RunAsync<T>()` and `ChatResponseFormat.ForJsonSchema<T>()`. Then add a **reconciliation guard** after extraction: the items plus the tax must equal the total. A mismatch marks the total `ToVerify` for a person; the guard never replaces the number the model read.

## Acceptance

- The output shows the sum of the lines (plus tax) next to the total read.
- A mismatch yields `ToVerify` and keeps the total as read; nothing is "corrected".
- `dotnet run -- --self-test` passes offline (scripted replies: one that adds up, one misread total, one with the tax line dropped).

## Key Concepts

- Multimodal input (image) + structured output (`RunAsync<T>`)
- Validation after extraction: an independent computation checks a single number
- Reporting a mismatch instead of repairing it

## Run

```
dotnet run
dotnet run -- --self-test
```

Configure a vision model under `VisionModel` in `appsettings.json` (Ollama `llava`, or OpenAI).
