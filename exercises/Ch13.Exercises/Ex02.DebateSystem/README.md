# Exercise 02 — Debate System (Intermediate)

## Prompt

A Proponent and a Skeptic debate "Should all new web services use microservices?" for three rounds. Build it twice:

- **A. Hand-rolled:** one shared `List<ChatMessage>`, appended after every turn, so each debater sees the whole debate.
- **B. Group Chat:** `AgentWorkflowBuilder.CreateGroupChatBuilderWith(...)` with a `RoundRobinGroupChatManager` and `MaximumIterationCount = 6`. The manager owns the log.

A Judge scores each transcript once ("Proponent: X/10, Skeptic: Y/10"); the code parses the scores.

**Accepted when** version B stops at the turn cap, both transcripts have the same structure (Proponent, Skeptic, six turns), and the token totals of both versions are printed.

## Running

```bash
dotnet run --project exercises/Ch13.Exercises/Ex02.DebateSystem                  # configured model
dotnet run --project exercises/Ch13.Exercises/Ex02.DebateSystem -- --self-test   # scripted model, RESULT n/n
```
