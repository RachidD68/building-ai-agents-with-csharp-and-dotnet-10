# Microsoft Foundry agent (preview)

Registers a Foundry-backed `AIAgent` through `AddFoundryAgent` from `AgenticAI.Common.Foundry`.
`Microsoft.Agents.AI.Foundry` and `Azure.AI.Projects` ship only as prereleases, so this project is
off the default path.

```bash
dotnet user-secrets set "LlmProvider:Foundry:ProjectEndpoint" "https://NAME.services.ai.azure.com/api/projects/PROJECT"
dotnet user-secrets set "LlmProvider:Foundry:Model" "<your model deployment>"
az login
dotnet run -- --config-check   # names and sources only; no call
dotnet run                     # one call to your Foundry project
```

`DefaultAzureCredential` signs you in, but your identity still needs a role assignment on the
Foundry project; signing in alone does not grant access.
