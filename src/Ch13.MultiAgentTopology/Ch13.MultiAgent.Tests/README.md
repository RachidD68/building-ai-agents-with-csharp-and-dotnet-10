# Chapter 11 -- Multi-Agent Tests

A token-free console assertion harness for testing multi-agent compositions. A fake `IChatClient` (`ScriptedChatClient`) returns canned responses, so agents behave deterministically and no model is required. No xUnit -- the companion solution does not standardize on a test framework, so this is a plain runnable program that returns a non-zero exit code on failure (CI-friendly).

## What it covers

| Test | What it asserts |
|------|-----------------|
| Deterministic output | A canned-response agent returns exactly the scripted text |
| Routing decisions | A scripted triage stub routes each message to the correct label |
| Golden transcript | A two-stage pipeline produces the exact expected final text |

## Running

```bash
dotnet run --project src/Ch11.MultiAgent/Ch11.MultiAgent.Tests
```

No Ollama or API key required -- the fake client never calls a model.
