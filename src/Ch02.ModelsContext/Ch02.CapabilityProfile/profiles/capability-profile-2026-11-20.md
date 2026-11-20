# Capability profile, 2026-11-20 10:55 UTC

Pins: .NET 10.0.15, Microsoft.Extensions.AI.Abstractions 10.11.0, OllamaSharp 5.4.25, OpenAI 2.14.0. Task set: 5 Contoso policy questions, 3 trial(s) each.

| Configuration | Provider | Model | Correct | Wrong | Invalid | Refused | Truncated | Transport | Provider error |
|---|---|---|---|---|---|---|---|---|---|
| local-json | ollama | llama3.2 | 12 | 3 | 0 | 0 | 0 | 0 | 0 |
| local-reasoning | ollama | llama3.2 | 0 | 0 | 0 | 0 | 0 | 0 | 15 |
| local-capped-text | ollama | llama3.2 | 0 | 0 | 0 | 0 | 15 | 0 | 0 |

## Options: requested and observed

| Configuration | Option | Requested | Evidence |
|---|---|---|---|
| local-json | MaxOutputTokens | 64 | honored: max 6 output tokens, 0 truncated |
| local-json | ResponseFormat | Json | 15/15 answered replies met the JSON contract |
| local-json | Temperature | 0.0 | sent; not observable from the response |
| local-reasoning | MaxOutputTokens | 64 | not observable: no usage reported |
| local-reasoning | Reasoning | Effort=Low | rejected: every call failed |
| local-reasoning | ResponseFormat | Json | 0/0 answered replies met the JSON contract |
| local-capped-text | MaxOutputTokens | 4 | honored: max 4 output tokens, 15 truncated |
| local-capped-text | Temperature | 0.7 | sent; not observable from the response |

## Failed trials

- local-json / taxi-64 #1: Wrong (finish: stop): expected review, got deny
- local-json / taxi-64 #2: Wrong (finish: stop): expected review, got deny
- local-json / taxi-64 #3: Wrong (finish: stop): expected review, got deny
- local-reasoning / taxi-42 #1: ProviderError: OllamaException: "llama3.2" does not support thinking
- local-reasoning / taxi-42 #2: ProviderError: OllamaException: "llama3.2" does not support thinking
- local-reasoning / taxi-42 #3: ProviderError: OllamaException: "llama3.2" does not support thinking
- local-reasoning / taxi-64 #1: ProviderError: OllamaException: "llama3.2" does not support thinking
- local-reasoning / taxi-64 #2: ProviderError: OllamaException: "llama3.2" does not support thinking
- local-reasoning / taxi-64 #3: ProviderError: OllamaException: "llama3.2" does not support thinking
- local-reasoning / limit-50 #1: ProviderError: OllamaException: "llama3.2" does not support thinking
- local-reasoning / limit-50 #2: ProviderError: OllamaException: "llama3.2" does not support thinking
- local-reasoning / limit-50 #3: ProviderError: OllamaException: "llama3.2" does not support thinking
- local-reasoning / self-approval #1: ProviderError: OllamaException: "llama3.2" does not support thinking
- local-reasoning / self-approval #2: ProviderError: OllamaException: "llama3.2" does not support thinking
- local-reasoning / self-approval #3: ProviderError: OllamaException: "llama3.2" does not support thinking
- local-reasoning / currency #1: ProviderError: OllamaException: "llama3.2" does not support thinking
- local-reasoning / currency #2: ProviderError: OllamaException: "llama3.2" does not support thinking
- local-reasoning / currency #3: ProviderError: OllamaException: "llama3.2" does not support thinking
- local-capped-text / taxi-42 #1: Truncated (finish: length): cut at the output cap: {"decision":"allow
- local-capped-text / taxi-42 #2: Truncated (finish: length): cut at the output cap: {"decision":"allow
- local-capped-text / taxi-42 #3: Truncated (finish: length): cut at the output cap: {"decision":"allow
- local-capped-text / taxi-64 #1: Truncated (finish: length): cut at the output cap: {"decision":"deny
- local-capped-text / taxi-64 #2: Truncated (finish: length): cut at the output cap: {"decision":"deny
- local-capped-text / taxi-64 #3: Truncated (finish: length): cut at the output cap: {"decision":"deny
- local-capped-text / limit-50 #1: Truncated (finish: length): cut at the output cap: {"decision":"allow
- local-capped-text / limit-50 #2: Truncated (finish: length): cut at the output cap: {"decision":"allow
- local-capped-text / limit-50 #3: Truncated (finish: length): cut at the output cap: {"decision":"allow
- local-capped-text / self-approval #1: Truncated (finish: length): cut at the output cap: {"decision":"deny
- local-capped-text / self-approval #2: Truncated (finish: length): cut at the output cap: {"decision":"deny
- local-capped-text / self-approval #3: Truncated (finish: length): cut at the output cap: {"decision":"deny
- local-capped-text / currency #1: Truncated (finish: length): cut at the output cap: {"decision":"deny
- local-capped-text / currency #2: Truncated (finish: length): cut at the output cap: {"decision":"deny
- local-capped-text / currency #3: Truncated (finish: length): cut at the output cap: {"decision":"deny

## Not run

- cloud-json (openai): not run; pass --include-cloud with LlmProvider:OpenAI:ApiKey set
