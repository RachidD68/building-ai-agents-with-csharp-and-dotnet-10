# Chapter 4 -- Setting Up Your Development Environment (First LLM Call)

Your first real conversation with an AI model. This project connects to an LLM provider using `Microsoft.Extensions.AI` and the multi-provider pattern from `AgenticAI.Common`.

## Prerequisites

- .NET 10 SDK
- An API key from at least one provider (see below)

## Setup (Step by Step)

### 1. Choose a Provider

| Provider | Cost | Best For |
|----------|------|----------|
| **Ollama** (local) | Free | Privacy, offline development, no account needed |
| **GitHub Models** | Free tier | Quick start with a GitHub account |
| **OpenAI** | Pay-as-you-go | Direct access, latest models |
| **Azure OpenAI** | Pay-as-you-go | Enterprise, managed deployments |

> **Anthropic (Claude)** is also wired up, but it returns an `AIAgent` (not an `IChatClient`), so it
> registers through `AddAnthropicAgent(...)` rather than `AddLlmChatClient(...)`. Set
> `LlmProvider:Anthropic:ApiKey` (or the `ANTHROPIC_API_KEY` environment variable). See Appendix A
> for the full Anthropic treatment.

### 2. Get Your API Key

**Ollama (recommended for getting started):**
1. Install Ollama from https://ollama.ai
2. Run: `ollama pull llama3.2`
3. No API key needed -- it runs locally

**GitHub Models:**
1. Go to https://github.com/marketplace/models
2. Generate a personal access token with `models:read` scope

**OpenAI:**
1. Sign up at https://platform.openai.com
2. Go to API Keys and create a new secret key

**Azure OpenAI:**
1. Create an Azure OpenAI resource in the Azure portal
2. Deploy a model (e.g., gpt-4o-mini)
3. Note your endpoint URL -- authentication uses DefaultAzureCredential

### 3. Configure User Secrets

User Secrets keep real credentials out of the repo. They live in your user profile, not the
project folder, so they can never be committed by accident. This project already has a
`<UserSecretsId>` (initialize a new one elsewhere with `dotnet user-secrets init`).

> **Important — Development environment only.** With `Host.CreateApplicationBuilder`, User Secrets
> load **only** when the environment is `Development`. A plain console app defaults to `Production`,
> so set the environment before you run, or your secrets are silently ignored:
>
> ```bash
> # PowerShell
> $env:DOTNET_ENVIRONMENT = "Development"
> # bash / zsh
> export DOTNET_ENVIRONMENT=Development
> ```
>
> (The `Properties/launchSettings.json` launch profile sets this for you when you run from an IDE.)

Navigate to the project directory and set your secrets:

```bash
cd src/Ch04.DevEnvironment/Ch04.FirstLlmCall
```

**For Ollama (default, no key needed):**
```bash
dotnet user-secrets set "LlmProvider:Provider" "ollama"
dotnet user-secrets set "LlmProvider:Ollama:Endpoint" "http://localhost:11434"
dotnet user-secrets set "LlmProvider:Ollama:Model" "llama3.2"
```

**For GitHub Models:**
```bash
dotnet user-secrets set "LlmProvider:Provider" "github"
dotnet user-secrets set "LlmProvider:GitHub:Token" "your-github-token"
dotnet user-secrets set "LlmProvider:GitHub:Model" "openai/gpt-4o-mini"
```

**For OpenAI:**
```bash
dotnet user-secrets set "LlmProvider:Provider" "openai"
dotnet user-secrets set "LlmProvider:OpenAI:ApiKey" "sk-your-key-here"
dotnet user-secrets set "LlmProvider:OpenAI:Model" "gpt-4o-mini"
```

**For Azure OpenAI:**
```bash
dotnet user-secrets set "LlmProvider:Provider" "azure"
dotnet user-secrets set "LlmProvider:AzureOpenAI:Endpoint" "https://your-resource.openai.azure.com/"
dotnet user-secrets set "LlmProvider:AzureOpenAI:Model" "gpt-4o-mini"
```

### 4. Run

```bash
dotnet run --project src/Ch04.DevEnvironment/Ch04.FirstLlmCall
```

## What Happens

1. The app loads configuration from User Secrets
2. It creates an `IChatClient` for your chosen provider (via `AgenticAI.Common`)
3. Sends a prompt and displays the complete response
4. Sends a second prompt and streams tokens in real-time as they arrive

## Troubleshooting

| Problem | Solution |
|---------|----------|
| "Missing configuration" error | Run the `dotnet user-secrets set` commands above |
| "Unknown LLM provider" | Check that `LlmProvider:Provider` is one of: azure, openai, github, ollama |
| Connection refused (Ollama) | Make sure Ollama is running: `ollama serve` |
| 401 Unauthorized | Your API key may be invalid or expired |
