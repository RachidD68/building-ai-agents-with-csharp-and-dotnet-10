# Ch02.CapabilityProfile

Runs one task set (five Contoso reimbursement-policy questions with known answers) against several model configurations and writes a dated profile, `profiles/capability-profile-yyyy-MM-dd.json` and `.md`, of what each configuration did.

Each trial lands in exactly one bucket:

| Outcome | Meaning |
|---|---|
| Correct / Wrong | The reply met the JSON contract (`{"decision":"allow"|"review"|"deny"}`); the decision did or did not match |
| Invalid | The reply finished normally but did not meet the contract |
| Refused | `FinishReason.ContentFilter`, or refusal text (a heuristic: the reply starts with "I can't", "I'm sorry", ...) |
| Truncated | `FinishReason.Length`: the output cap cut the reply. Read before the text, so a cut reply is never called invalid |
| TransportError | The call never got an answer (`HttpRequestException`, a timeout, an I/O error) |
| ProviderError | The provider answered with an error (for example, a model that rejects an option) |

The caller's own cancellation stops the run; it is not counted.

For each option a configuration sends, the profile says what the responses show: an output cap honored or ignored (from `UsageDetails.OutputTokenCount`), reasoning observed (`TextReasoningContent` or `ReasoningTokenCount`) or not, the share of replies that met the JSON contract. Temperature, top-p and seed are sent but cannot be observed from a response, and the profile says so.

## Running

```bash
dotnet run --project src/Ch02.ModelsContext/Ch02.CapabilityProfile -- --self-test    # scripted fake client, no network
dotnet run --project src/Ch02.ModelsContext/Ch02.CapabilityProfile                   # three local configurations (Ollama), 3 trials per task
dotnet run --project src/Ch02.ModelsContext/Ch02.CapabilityProfile -- --trials 5
dotnet run --project src/Ch02.ModelsContext/Ch02.CapabilityProfile -- --include-cloud  # adds cloud-json (OpenAI); needs LlmProvider:OpenAI:ApiKey and :Model; costs money
```

The live run needs the environment from the development-environment chapter (Ollama with `llama3.2` pulled). Without `--include-cloud`, the cloud configuration is listed under "Not run" instead of being skipped silently.

## Checked-in profile

`profiles/capability-profile-2026-11-20.*` is the run printed in the chapter (Ollama `llama3.2`, OllamaSharp 5.4.25, Microsoft.Extensions.AI.Abstractions 10.11.0). Rerun and check in a new dated profile when the model, a package pin or the task set changes; never edit an old one.

## The scripted client

`ScriptedChatClient` plays one step per call (a response or an exception) and records the options it received. The first-agent chapter builds this kind of fake step by step.
