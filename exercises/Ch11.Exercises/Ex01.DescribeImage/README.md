# Exercise 01 — Describe an Image: DataContent vs UriContent (Basic)

## Prompt

Send a local image to a vision agent with `DataContent` and print the description. Then add a second run that sends the same image question as a `UriContent` hosted URL, and compare what each path returns on your configured model. The lesson is in the difference.

## What it does

- **Run 1 — local bytes**: loads the image from disk with `File.ReadAllBytesAsync` and attaches it as a `DataContent` part (the bytes are inlined into the request). Works on every vision provider, including local Ollama `llava`.
- **Run 2 — hosted URL**: attaches a stable public image URL as a `UriContent` part. The **model** fetches the bytes itself — this requires a provider that fetches URLs server-side (cloud `gpt-5.6-terra` does; local Ollama does not), so on Ollama this run prints a clear `[skipped]` message instead of crashing.
- Both runs use the **same question**, are timed with `Stopwatch`, and the outputs are printed side by side at the end.

## Key Concepts

- Multimodal `ChatMessage` with `TextContent` + `DataContent` / `UriContent`
- `DataContent`: bytes inlined into the request (local files, air-gapped images)
- `UriContent`: the model fetches the URL (cloud providers only)
- Timing and comparing the two content paths on the same agent

## Run

```
dotnet run -- "C:\path\to\image.png"
```

Configure a vision model under `VisionModel` in `appsettings.json` (Ollama `llava`,
or cloud `gpt-5.6-terra`). Local: `ollama pull llava`. On Ollama, expect the `UriContent`
run to be reported as skipped — that asymmetry is the point of the exercise.
