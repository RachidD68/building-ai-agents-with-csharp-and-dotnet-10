# Ch16.CodingAgent — a coding agent scoped to a scratch repo

A MAF agent with three tools over one folder: `list_files`, `read_file` and
`write_file`.

- `ScratchScope` resolves every path the model sends: relative only, inside the
  root after normalization, never under `.git`, never through a symbolic link or
  junction. A scope limits what the tools do; it is not an operating-system sandbox.
- `write_file` is an `ApprovalRequiredAIFunction`: each write comes back as a
  `ToolApprovalRequestContent`, and the scope is checked again when an approved call
  runs, so an approval never widens it.

## About the GitHub Copilot provider

The chapter drives this agent through MAF's GitHub Copilot provider
(`Microsoft.Agents.AI.GitHub.Copilot`, which depends on `GitHub.Copilot.SDK`). That
package was not in the offline package cache when this project was written, so it is
not referenced and no code here calls Copilot. The live run uses the configured
provider (Ollama by default). The provider is a local agent; it is not GitHub's cloud
Copilot coding agent, which turns an issue into a pull request.

## Running

```bash
# offline checks (no model, no Copilot call)
dotnet run --project src/Ch16.SkillsHarnesses/Ch16.CodingAgent -- --self-test

# live run in a fresh scratch copy under the temp folder; you approve each write
dotnet run --project src/Ch16.SkillsHarnesses/Ch16.CodingAgent
```

The symbolic-link check in the self-test runs only where the account may create a
link (it is skipped, and not counted, when the OS refuses).
