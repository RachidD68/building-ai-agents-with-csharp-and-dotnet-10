# Exercise 02 — Session Manager (Intermediate)

## Prompt

Build a console app that supports **multiple named sessions**. The user can:

- `/new <name>` — Create a new session
- `/switch <name>` — Switch to an existing session
- `/list` — List all sessions
- `/delete <name>` — Delete a session
- Type normally to chat in the current session

## Hints

- Use a `Dictionary<string, AgentSession>` to track sessions.
- Each session maintains its own conversation history.
- Save session metadata to JSON files for persistence info.

## Expected Outcome

```
[no session] You: /new coding
  Created and switched to session: coding

[coding] You: What is LINQ?
  Assistant: LINQ is...

[coding] You: /new writing
  Created and switched to session: writing

[writing] You: /list
  Active sessions:
    - coding
    - writing (current)
```

## Key Concepts

- Multiple concurrent `AgentSession` instances
- Session lifecycle management
- Command parsing in console apps
- JSON file persistence
