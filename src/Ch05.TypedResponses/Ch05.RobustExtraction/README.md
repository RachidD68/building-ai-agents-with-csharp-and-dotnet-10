# Ch05.RobustExtraction — Structured Output, Hardened for Production

The production counterpart to the happy path. Three ideas, together:

1. **`[Description]` instead of XML doc comments.** XML `<summary>` comments only
   reach the generated schema when the project sets
   `<GenerateDocumentationFile>true</GenerateDocumentationFile>`. Turn that off and
   the hints vanish *silently* — no error, just a weaker schema. `[Description]`
   from `System.ComponentModel` is honored by `AIJsonUtilities` regardless of build
   configuration, which makes it the build-independent default for production.
2. **`TryRunStructuredAsync<T>()`.** A reusable extension that wraps `RunAsync<T>()`
   with a single stricter retry when the reply cannot be read (`JsonException`, or
   `InvalidOperationException` for a reply with no JSON at all). A reply the provider
   cut off (`ChatFinishReason.Length`) or filtered (`ChatFinishReason.ContentFilter`)
   is not retried. It returns a `Success<T>` / `Failure<T>` discriminated union; a
   failure keeps the raw text of the reply that failed and the finish reason. It
   never loop-retries: one retry caps the token burn.
3. **Soft validation.** Confidence thresholds and cross-field invariants flag
   suspect extractions for review. They never silently "fix" the model's output.

## Self-reported confidence is not a probability

`InvoiceExtraction.SelfReportedConfidence` is the model's own opinion of its
answer, not a calibrated score, and the console labels it that way. It is
`double?` so a missing field reads as unknown rather than a silent `0`, and it
carries `[JsonNumberHandling(Strict)]` so a string such as `"0.9"` or `"NaN"` is
rejected even though `JsonSerializerOptions.Web` reads other numbers from
strings. `ConfidenceCheck` flags anything outside 0.0..1.0 (including `95` on a
percent scale) and never trusts it. Confidence can only *add* a review warning:
a 95% on a half-empty extraction still routes to manual review.

## What You'll Learn

- Build-independent schema hints with `[Description]`
- A retry-once wrapper around the typed call that names truncated and filtered replies, with `ILogger` at the boundaries
- Pattern-matching a `Result<T>` discriminated union
- Soft validation that flags rather than corrects

## Run

```bash
dotnet run
dotnet run -- --self-test   # offline: canned JSON and a fake chat client, no LLM
```

## A note on the captured output

The committed output was produced against a local `llama3.2` via Ollama. The JSON
is always well-formed (so the retry/fallback path is not exercised here), but the
model leaves `vendor` empty and misses an amount — and the soft validators catch
exactly those gaps. That is the lesson: the typed call guarantees *shape*, and the
validators guard *meaning*. A frontier model extracts these fields far more
reliably; swap the provider in `appsettings.json` to compare.
