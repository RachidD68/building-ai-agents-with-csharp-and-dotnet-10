# Ch15.ContentSafety — Graduating from regex (Azure AI Content Safety)

The production layer above the chapter's regex filters. Part of Microsoft
Foundry; ML-backed harm detection plus managed custom blocklists. Backs
Chapter 15's "Azure AI Content Safety" section.

## What It Shows

Calls the **stable, typed** surface of `Azure.AI.ContentSafety` 1.0.0:

- `ContentSafetyClient.AnalyzeText(AnalyzeTextOptions)` — per-category severity
  (0–6) for **Hate / Violence / Sexual / SelfHarm**.
- A **custom blocklist** via `AnalyzeTextOptions.BlocklistNames`.

Run against the same adversarial-flavoured inputs as the regex demo to compare
recall: regex matches fixed patterns; Content Safety scores semantic harm.

## Honest capability note

**Prompt Shields** (direct + document/indirect injection) and **Groundedness
detection** are real Content Safety features, but in the **1.0.0 .NET SDK they
are not exposed as typed methods** — they are reached via the REST API only.
This project does not fabricate a typed call that does not exist. For those
features, call the REST endpoint (see the chapter's ⚠️ Warning).

## Configuration (gated — runs offline without it)

Without an endpoint + key the program prints a clear `[skipped]` note and exits
0, so it builds and runs offline. To run for real, set:

```
ContentSafety:Endpoint   https://<resource>.cognitiveservices.azure.com/
ContentSafety:ApiKey     <key from the resource>
ContentSafety:BlocklistName   (optional) a blocklist you created
```

via `appsettings.json`, user-secrets, or environment variables.

## Running

```bash
dotnet run
```
