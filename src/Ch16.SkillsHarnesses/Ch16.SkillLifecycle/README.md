# Ch16.SkillLifecycle — validate, approve, pin, withdraw

A SKILL.md is code the model follows: edit it and the agent behaves differently on
the next run. This project puts a gate between the skill folder and the provider.

| Piece | What it does |
|-------|--------------|
| `SkillRegistry.ValidateAsync` | owner and version from the frontmatter `metadata`, every `references/`, `scripts/` or `assets/` path in the body present, SHA-256 of the effective content (every file in the skill folder, line endings normalized to LF) |
| `SkillRegistry.Approve` / `skills.lock.json` | the reviewed state: name, owner, version, origin, hash, reviewer |
| `GatedSkillsSource` | a `DelegatingAgentSkillsSource`: activates a skill only when its current hash matches an approved, non-withdrawn record; pins it for the run opened with `BeginRun`; logs every load, resource read and script lookup with the hash |
| `SkillRegistry.Withdraw` | emergency stop, checked on every operation, so it reaches runs in flight |

`AgentSkillsProvider` asks its source for skills before every model call and every
skill tool call, not once per run. That is why the pin is held per run with
`BeginRun`: without it, a skill approved again in the middle of a run would change
under that run.

## Running

```bash
# offline checks (scripted client)
dotnet run --project src/Ch16.SkillsHarnesses/Ch16.SkillLifecycle -- --self-test

# validate skills/ and rewrite skills.lock.json (run from the project folder)
dotnet run -- --approve <reviewer>

# one gated run against the configured provider (Ollama by default)
dotnet run
```

To see the gate refuse a change: edit `skills/refund-escalation/SKILL.md`, run
`dotnet run` (the run log shows `refused  hash mismatch`), then `--approve` and run again.
