# Exercise 01 — Conversation Logger (Beginner)

## Prompt

Build a multi-turn console chat that logs every message (user + assistant) to a text file. Use `AgentSession` for conversation memory so the agent remembers previous turns.

## Hints

- `AgentSession` maintains conversation history across `RunAsync` calls.
- Use `File.AppendAllTextAsync` to log each message with a timestamp.
- Include role labels (USER, ASSISTANT, SYSTEM) in the log.

## Expected Outcome

A log file like:
```
[2026-03-16 10:30:00] [SYSTEM] Session started at 2026-03-16T10:30:00
[2026-03-16 10:30:05] [USER] What is dependency injection?
[2026-03-16 10:30:07] [ASSISTANT] Dependency injection is...
[2026-03-16 10:30:15] [USER] Can you give me an example?
[2026-03-16 10:30:17] [ASSISTANT] Sure, here's an example...
[2026-03-16 10:30:20] [SYSTEM] Session ended. 2 turns.
```

## Key Concepts

- `AgentSession` for multi-turn memory
- File-based conversation logging
- Async file I/O with `CancellationToken`
