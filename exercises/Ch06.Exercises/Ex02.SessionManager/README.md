# Exercise 02 — Session Manager (Intermediate)

## Prompt

Build a small SessionManager that keeps multiple named sessions, each persisted to its own JSON file under a **configurable folder**, with `/new`, `/switch`, `/list`, and `/delete` commands in a looped console chat. Persist with `SerializeSessionAsync` — and use resolver-backed `JsonSerializerOptions` (copy `AgentAbstractionsJsonUtilities.DefaultOptions`), or you'll hit a `NotSupportedException` on the first save.

## Hints

- A `Dictionary<string, AgentSession>` keyed by name.
- Watch the case where the user switches to an id whose file doesn't exist yet — load-or-create, don't assume.
- `DeserializeSessionAsync` rebuilds a session (conversation included) from the saved `JsonElement` — that's what makes a conversation resume after a restart.
- The folder comes from `SessionManager:Directory` in `appsettings.json`.

## Expected Outcome

```
[no session] You: /new coding
  Created session 'coding' (saved to ...\sessions\coding.json)

[coding] You: My favourite language is C#.
  Assistant: Noted! ...

# restart the app
Saved sessions found on disk: coding

[no session] You: /switch coding
  Resumed session 'coding' from disk (2 messages restored).

[coding] You: What is my favourite language?
  Assistant: C#! ...
```

## Key Concepts

- Real session persistence: `SerializeSessionAsync` / `DeserializeSessionAsync`
- Resolver-backed `JsonSerializerOptions` (`AgentAbstractionsJsonUtilities.DefaultOptions`)
- One JSON file per named session; load-or-create on `/switch`
- Conversations that survive app restarts
