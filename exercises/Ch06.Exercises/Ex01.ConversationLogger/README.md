# Exercise 01 — Conversation Logger (Basic)

## Prompt

Extend the `Ch06.AgentSessionDemo` program to write every turn to a log file in real time. Each log line should contain a **timestamp, the session ID, the role, and the message text**. The log survives across runs; the in-memory history does not.

## Hints

- `File.AppendAllTextAsync` in a helper method invoked after each turn. You don't need a real logger for this — `ILogger<Program>` with a file provider is Chapter 15's job.
- `AgentSession` has no built-in id for client-side history, so assign each session a short id of your own — the log then distinguishes session 1's turns from session 2's.
- Append to a fixed file name so the log accumulates across runs.

## Expected Outcome

A log file like:

```
2026-07-24 10:30:05.123 [a1b2c3d4] [user     ] My name is Rachid. I live in Seattle...
2026-07-24 10:30:07.456 [a1b2c3d4] [assistant] Nice to meet you, Rachid! ...
2026-07-24 10:30:12.789 [9f8e7d6c] [user     ] What is my name?
2026-07-24 10:30:14.012 [9f8e7d6c] [assistant] I don't have that information...
```

Run the program twice: the file keeps growing, while each run's in-memory history starts empty.

## Key Concepts

- `InMemoryChatHistoryProvider` wired via `ChatClientAgentOptions`
- Real-time per-turn logging with timestamp + session id + role + text
- Durable log file vs volatile in-memory history
