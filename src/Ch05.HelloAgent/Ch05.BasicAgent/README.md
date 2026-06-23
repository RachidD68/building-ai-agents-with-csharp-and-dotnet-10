# Ch05.BasicAgent — Hello, Agent!

Your first agent built with the Microsoft Agent Framework (MAF). Demonstrates creating a `ChatClientAgent` with a pirate persona, sending a message, and getting a complete response.

## What You'll Learn

- Creating agents with `ChatClientAgent` (constructor and options patterns)
- Using the `IChatClient.AsAIAgent()` extension method
- System prompts (instructions) and persona design
- Stateless one-shot calls — `RunAsync()` with no session (the agent is stateless by default)
- Overriding chat options per call with `ChatClientAgentRunOptions` (same agent, two temperatures)
- Handling real provider failures (`ClientResultException`: 429, content filter, transient 5xx), not just cancellation
- Non-streaming agent responses via `RunAsync()`

## Setup

```bash
cd src/Ch05.HelloAgent/Ch05.BasicAgent
dotnet user-secrets set "LlmProvider:Provider" "github"
dotnet user-secrets set "LlmProvider:GitHub:Token" "<your-token>"
```

## Run

```bash
dotnet run
```
