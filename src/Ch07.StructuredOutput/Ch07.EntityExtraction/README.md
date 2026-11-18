# Ch07.EntityExtraction — Extracting Structured Data from Text

Extracts structured entities from free-text customer feedback, including sentiment, urgency, categories, and suggested actions.

## What You'll Learn

- Real-world entity extraction with enums (`Sentiment`, `Urgency`)
- Validation of extracted data and edge-case handling
- Processing multiple feedback samples with varying complexity
- Quality indicators (self-reported confidence, extraction warnings)

## Self-reported confidence is not a probability

`FeedbackAnalysis.SelfReportedConfidence` is the model's own opinion of its
answer, not a calibrated score, and the console labels it that way. A missing
value reads as `null` (unknown), a string or NaN is rejected by the parser, and
`ConfidenceCheck` flags anything outside 0.0..1.0. Confidence can only *add* a
review warning; it never clears one, so it is never the sole gate.

## Run

```bash
dotnet run
dotnet run -- --self-test   # offline: JSON parsing only, no LLM
```
