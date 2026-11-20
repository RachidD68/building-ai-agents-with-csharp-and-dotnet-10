# Ch12.ReceiptExtraction

A receipt photo becomes a typed proposal that the reimbursement policy can judge, and nothing the model cannot read is filled in.

```
page images -> vision model, one ReceiptReading per page (RunAsync<T>, JSON schema)
            -> ReceiptValidator: field states, arithmetic, domain rules
            -> ReceiptProposal -> ReceiptHandoff -> Ch08.ActionPolicy decides
                               -> blocked: a person verifies first
```

| File | What it holds |
|---|---|
| `ReceiptModels.cs` | `FieldState` (Present, Absent, Unreadable, ToVerify), the model's `ReceiptReading`, the validated `ReceiptProposal` and its `Blocking` list |
| `ReceiptValidator.cs` | validation after extraction, and `ReceiptHandoff.ToProposal` to the policy's `ReimbursementProposal` |
| `ReceiptReader.cs` | one agent run per page image (`DataContent`), and the `VisionModel` client |
| `Program.cs` | the live run over the corpus, and the self-test |

## The rules

- The model returns each value as printed text plus a state (`present`, `absent`, `unreadable`). The application parses it. An amount that is not digits with an optional two-digit decimal part (`64,8O`, `64.80 EUR`) is `ToVerify` with no value.
- An unreadable field stays `Unreadable` with no value. No rule ever replaces a value.
- The lines must add up to the total, and a second printed amount (amount charged) must equal it; otherwise the total is `ToVerify`. A total with no checkable lines is `ToVerify` too.
- The currency must be a three-letter code (the policy's `CurrencyCode`), the date `yyyy-MM-dd` and not after today.
- Each page is sent as its own image, so the page number of every field comes from the application. The model is not asked for coordinates: a region it writes without a coordinate system defined by the provider is not provenance.
- Text printed on the receipt is data. The model is told to quote instructions in `printedInstructions`; any quoted instruction blocks the hand-off (`instruction_on_document`).
- The hand-off happens only when total, currency and date are `Present` and nothing is quoted. The employee id comes from the caller's account, never from the receipt (the hotel invoice prints `Guest: E-1001`; it is not used).

What validation cannot catch: a model that invents a whole set of lines and a total that agree with each other. The arithmetic catches inconsistency, not a consistent invention; the live run's score reports how often that happens on the corpus.

## Running

```bash
dotnet run --project src/Ch12.MultimodalDocuments/Ch12.ReceiptExtraction                  # Ollama llava by default
dotnet run --project src/Ch12.MultimodalDocuments/Ch12.ReceiptExtraction -- --self-test   # scripted replies, no network
```

The self-test sends the real corpus images through the real `ReceiptReader` and agent; only the chat client is a fake, whose replies are written by hand to stand for model behaviours (an honest read, an invented total, an obeyed instruction). It tests the validation and the hand-off, not a vision model. The live run compares the model's totals with `assets/receipts/corpus.json` and counts totals that were right, sent to a person, or silently wrong.

No OCR or document-intelligence package is used: the local path stays GA and offline.
