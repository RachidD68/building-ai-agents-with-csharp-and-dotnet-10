# Ch20.ContentSafety: Azure AI Content Safety as one more signal (optional)

Azure only. Uses the typed surface of `Azure.AI.ContentSafety` 1.0.0
(`ContentSafetyClient.AnalyzeTextAsync`, per-category severity, custom
blocklists) and turns the result into a `ModerationSignal`: flagged or clear,
with the reason. A missing severity is not treated as clear.

Prompt Shields and groundedness detection are not typed methods in the 1.0.0
.NET SDK (they are REST calls); this sample does not make them.

## Configuration

Without `ContentSafety:Endpoint` the live run prints `[skipped]` and exits 0.
With it, auth is `DefaultAzureCredential`, or `ContentSafety:ApiKey` when set.
Optional: `ContentSafety:BlocklistName`.

## Running

```bash
dotnet run -- --self-test   # offline: options and signal mapping, no Azure call
dotnet run                  # calls Azure when configured
```

The Azure path compiles; it has not been run for the book (no resource configured).
