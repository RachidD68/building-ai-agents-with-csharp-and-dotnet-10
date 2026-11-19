# Exercise 01 — Persona Switcher (Basic)

## Prompt

Build a console app that lets the user type a persona description (e.g., "sarcastic support engineer") followed by a question, and spins up a **fresh `ChatClientAgent`** with those instructions for each question. Reuse the `IChatClient` across calls; only the agent changes.

## Hints

- The instructions constructor accepts any string — the typed persona description goes into the system prompt verbatim.
- Users providing their own prompts introduces an injection vector — for this exercise don't worry about it, but Chapter 16 shows how to sanitize.
- A single question needs no session: omit it and MAF creates a throwaway session for the call.

## Expected Outcome

```
Persona: sarcastic support engineer
Question: Why is my build failing?

[sarcastic support engineer]: Oh, the build is failing? Groundbreaking. Have you tried reading the error message...
```

## Key Concepts

- One `IChatClient`, many agents — agents are cheap; connections are not
- Persona via the instructions constructor
- Session-less single-turn `RunAsync`
