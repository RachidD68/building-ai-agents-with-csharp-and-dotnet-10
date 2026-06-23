# Exercise 01 — Multi-Provider Chat (Beginner)

## Prompt

Build an interactive console chat that:

1. Prompts the user to pick a provider: **azure**, **openai**, **github**, or **ollama**.
2. Creates the appropriate `IChatClient` using User Secrets for configuration.
3. Allows single-turn conversations with token usage display.

## Setup

Configure User Secrets:

```bash
dotnet user-secrets set "LlmProvider:GitHub:Token" "your-token-here"
dotnet user-secrets set "LlmProvider:GitHub:Model" "gpt-4o-mini"
```

## Hints

- Use `Microsoft.Extensions.AI.OpenAI` for the `AsIChatClient()` extension method.
- Azure uses `DefaultAzureCredential`; OpenAI/GitHub use API keys.
- Ollama uses `OllamaApiClient` with a local endpoint.
- `ChatResponse.Usage` contains token counts (may be null for some providers).

## Expected Outcome

```
Select provider: github
Connected to: github
Type your message (or 'quit' to exit):

You: What is LINQ?
Assistant: LINQ (Language Integrated Query) is...

  [Tokens: 12 in, 85 out, 97 total]
```

## Key Concepts

- Multi-provider `IChatClient` abstraction
- User Secrets for secure API key storage
- `ChatResponse` usage metadata
- Provider-agnostic chat interface
