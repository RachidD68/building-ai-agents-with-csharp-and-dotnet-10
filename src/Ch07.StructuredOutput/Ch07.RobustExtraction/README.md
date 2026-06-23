# Ch07.RobustExtraction — Structured Output, Hardened for Production

The production counterpart to the happy path. Three ideas, together:

1. **`[Description]` instead of XML doc comments.** XML `<summary>` comments only
   reach the generated schema when the project sets
   `<GenerateDocumentationFile>true</GenerateDocumentationFile>`. Turn that off and
   the hints vanish *silently* — no error, just a weaker schema. `[Description]`
   from `System.ComponentModel` is honored by `AIJsonUtilities` regardless of build
   configuration, which makes it the build-independent default for production.
2. **`TryRunStructuredAsync<T>()`.** A reusable extension that wraps `RunAsync<T>()`
   with a single stricter retry on `JsonException`, then an untyped fallback that
   logs the raw response. It returns a `Success<T>` / `Failure<T>` discriminated
   union and never loop-retries — one retry caps the token burn.
3. **Soft validation.** Confidence thresholds and cross-field invariants flag
   suspect extractions for review. They never silently "fix" the model's output.

## What You'll Learn

- Build-independent schema hints with `[Description]`
- A retry/repair → fallback wrapper around the typed call, with `ILogger` at the boundaries
- Pattern-matching a `Result<T>` discriminated union
- Soft validation that flags rather than corrects

## Run

```bash
dotnet run
```

## A note on the captured output

The committed output was produced against a local `llama3.2` via Ollama. The JSON
is always well-formed (so the retry/fallback path is not exercised here), but the
model leaves `vendor` empty and misses an amount — and the soft validators catch
exactly those gaps. That is the lesson: the typed call guarantees *shape*, and the
validators guard *meaning*. A frontier model extracts these fields far more
reliably; swap the provider in `appsettings.json` to compare.
