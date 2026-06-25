# Ch16.AgentSkills — File-based, inline, and class-based Agent Skills

Backs Chapter 16's "Agent Skills" content. A single `SkillsAgent` surfaces three
different skill styles through one `AgentSkillsProvider`, and Microsoft Agent
Framework automatically injects the `load_skill` / `read_skill_resource` /
`run_skill_script` tools so the model can discover and use them on demand. The
streaming loop prints every one of those tool calls so you can watch
*progressive disclosure* happen.

## The three skill sources

| Style | How it's defined | Example in this project |
|-------|------------------|-------------------------|
| **File-based** | A `skills/<name>/` directory on disk with a `SKILL.md` and optional `references/` files the agent reads on demand | `skills/expense-report/` (with `references/POLICY_FAQ.md`) |
| **Inline** | An `AgentInlineSkill` defined in code with a static resource | `code-style` — the team's coding conventions |
| **Class-based** | An `AgentClassSkill<...>` with `[AgentSkillResource]` data and an `[AgentSkillScript]` that runs in-process | `UnitConverterSkill` — a conversion table + a multiply script |

`AgentSkillsProviderBuilder` mixes the three sources into one provider and wires
it through `ChatClientAgentOptions.AIContextProviders`. The on-disk `skills/`
tree is copied to the build output by the `.csproj`.

## Key concepts

| API | Role |
|-----|------|
| `AgentSkillsProvider` / `AgentSkillsProviderBuilder` | Aggregates skill sources and exposes them to the agent |
| `AgentInlineSkill` | A skill declared entirely in code |
| `AgentClassSkill<T>`, `[AgentSkillResource]`, `[AgentSkillScript]` | A strongly-typed skill with bundled resources and an executable script |
| `load_skill` / `read_skill_resource` / `run_skill_script` | The tools MAF injects so the model can use skills progressively |

## Configuration

Runs against **Ollama** (the default, no key) or any configured provider — see
the repository root README and Chapter 4. No extra configuration is required to
run the demo locally.

## Running

```bash
dotnet run --project src/Ch16.AgentSkills/Ch16.AgentSkills
```
