# Magentic orchestrator (online only)

> **Online only.** This project is kept in the repository but is not printed in the book: the multi-agent chapter gives Magentic one paragraph and points here. It builds with the solution like every other project.

A manager agent keeps a ledger of facts, plans and observations and coordinates three specialists (Researcher, Writer, Reviewer) toward a short technical brief. See the header of `Program.cs` for how the Magentic team is declared.

```bash
dotnet run --project src/Ch13.MultiAgentTopology/Ch13.MagenticOrchestrator
```

It needs a model provider (see the repository README). Expect a longer run than the other orchestration samples: the manager plans, delegates and re-checks each step.
