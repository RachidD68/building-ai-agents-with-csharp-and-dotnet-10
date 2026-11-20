# Ch04.BasicAgent

Contoso's expense-policy assistant as a first `ChatClientAgent`: it answers questions and takes no action.

- `ContosoPolicy.cs`: the agent and its instructions. The other first-agent projects, the tests and the Contoso v0 snapshot compile this file in as it is.
- `Program.cs`: the three ways to build the agent (constructor with instructions, `ChatClientAgentOptions` with pinned `ChatOptions`, the `AsAIAgent` extension), a run with no session, a run with a session, and `ChatClientAgentRunOptions` overriding the options for one call (a 12-token ceiling, so the finish reason shows `length`).
- Provider failures are caught by type (`HttpRequestException` for Ollama and other HTTP providers, `ClientResultException` for the OpenAI-compatible SDKs); Ctrl+C cancels the token that reaches the provider call.

```bash
dotnet run --project src/Ch04.FirstAgent/Ch04.BasicAgent     # Ollama llama3.2 by default
```

The offline proof is `Ch04.AgentTests`.