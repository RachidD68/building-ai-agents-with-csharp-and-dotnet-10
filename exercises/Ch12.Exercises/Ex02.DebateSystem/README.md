# Exercise 02 — Debate System (Intermediate)

## Prompt

Build a two-agent debate: a "Proponent" that argues for a position and a
"Skeptic" that challenges it. Run three rounds (Proponent → Skeptic →
Proponent → Skeptic → ...). Each agent sees the full history and responds to
the prior argument. Topic: "Should all new web services use microservices?"

A Judge agent scores every round ("Proponent: X/10, Skeptic: Y/10"); the code
parses the real scores and prints the totals and the winner at the end.

## Key Concepts

- Hand-rolled group chat: ONE shared `List<ChatMessage>` conversation log
- Every turn passes the full log, not just the previous argument
- Judge/evaluator agent pattern with parsed "X/10" scores (regex, not proxies)
- Round-based conversation management

## Follow-up (from the book)

Rebuild the same debate with the built-in Group Chat orchestration —
`AgentWorkflowBuilder.CreateGroupChatBuilderWith(...)` plus a
`RoundRobinGroupChatManager` — and compare lines of code, behavior, and token
cost. See `src/Ch12.MultiAgent/Ch12.GroupChatOrchestration` for the pattern;
the message-list bookkeeping in this solution disappears entirely.
