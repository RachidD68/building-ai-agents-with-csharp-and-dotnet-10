# Exercise 01 — Describe a Local Image (Basic)

## Prompt

Point a vision agent at a local image file and have it describe what it sees. Load
the image from disk as **bytes** and attach it to a `ChatMessage` as a `DataContent`
part alongside a `TextContent` question.

## Key Concepts

- Multimodal `ChatMessage` with `TextContent` + `DataContent`
- Loading a local file with `File.ReadAllBytes` (the bytes are inlined into the request)
- Choosing the correct image media type (`image/png`, `image/jpeg`, ...)
- Reusing the Chapter 5 `RunAsync` against a vision-capable model

## Run

```
dotnet run -- "C:\path\to\image.png"
```

Configure a vision model under `VisionModel` in `appsettings.json` (Ollama `llava`,
or cloud `gpt-4o`). Local: `ollama pull llava`.
