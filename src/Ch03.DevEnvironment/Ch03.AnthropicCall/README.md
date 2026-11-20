# Anthropic (Claude) call

The first caller of `AddAnthropicAgent` (from `AgenticAI.Common.Anthropic`, over the GA package
`Microsoft.Agents.AI.Anthropic`). Anthropic returns an `AIAgent`, not an `IChatClient`, so it has
its own registration instead of an arm in `AddLlmChatClient`.

```bash
dotnet user-secrets set "LlmProvider:Anthropic:ApiKey" "<your key>"   # or ANTHROPIC_API_KEY
dotnet run -- --config-check   # names and sources only; no call
dotnet run                     # one call, billed to your Anthropic account
```

There is no offline model behind this project, so its only offline check is `--config-check`.
