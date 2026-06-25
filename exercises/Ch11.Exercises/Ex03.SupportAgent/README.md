# Exercise 03 — Multimodal Support Agent (Challenge)

## Prompt

Build the chapter's real-world opener as a runnable tool: a support agent that reads a
**screenshot** and answers a question about it. The user pastes an error dialog and asks
"what went wrong and how do I fix it?"; the agent gets *both* the image and the question
in a single multimodal `ChatMessage` and replies with a diagnosis grounded in what is
actually on the screen.

A sample screenshot (`assets/error-screenshot.png`, a database connection-timeout dialog)
is included. Pass your own with arguments:

```
dotnet run -- "C:\path\to\screenshot.png" "Why am I seeing this?"
```

## Key Concepts

- Multimodal input: a `ChatMessage` carrying `TextContent` (the question) + `DataContent` (the screenshot bytes)
- Grounding the answer in the image — quoting the error code it can actually read, not inventing details
- The same `agent.RunAsync(...)` from Chapter 5 — only the message is richer

## Stretch

Swap the image for an audio file using the **transcribe-then-prompt** pattern from the
chapter: transcribe the clip with a speech-to-text service (Azure AI Speech / Whisper),
then send the transcript as `TextContent` to the same agent. The agent code does not change.

## Run

```
dotnet run
```

Configure a vision model under `VisionModel` in `appsettings.json` (Ollama `llava`, or
cloud `gpt-4o`). Local: `ollama pull llava`. A frontier model gives crisper diagnoses.
