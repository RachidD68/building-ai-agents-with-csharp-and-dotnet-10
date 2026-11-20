# Exercise 01 — Multi-Provider Chat (Basic)

## Prompt

Build a console chat whose provider is chosen by **configuration only**:

1. Read `LlmProvider:Provider` (`ollama`, `openai` or `azure`; `ollama` when nothing sets it).
2. Build the matching `IChatClient`; a missing setting fails with its **name**, never its value.
3. `--config-check` prints the active provider and the source it came from, then exits.

## Acceptance

- No code change between two runs on different providers: switch with a user secret or an
  environment variable (`LlmProvider__Provider=openai`).
- `--config-check` names the active provider.
- No secret appears in the output or in any file of the project.

## Setup

```bash
dotnet user-secrets set "LlmProvider:Provider" "openai"
dotnet user-secrets set "LlmProvider:OpenAI:ApiKey" "<your key>"
```

User secrets load only when `DOTNET_ENVIRONMENT` is `Development` (the launch profile sets it).

## Run

```bash
dotnet run -- --self-test      # offline, with the fake client in SelfTest.cs: RESULT n/n
dotnet run -- --config-check
dotnet run
```

The fake client (`FakeChatClient`) only proves the chat turn here; the next chapter explains
scripted clients and the tests built on them.
