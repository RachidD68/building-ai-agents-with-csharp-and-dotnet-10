# Ch20.InputSanitization: injection and PII detectors, measured

Two regex detectors (`Sanitizers/`): `PromptInjectionDetector` for user input and
`PiiDetector` (email, phone, card, SSN, IPv4) with redaction.

The self-test runs a labelled corpus and prints, for each sample, whether the
detector caught it, missed it, or flagged a benign question. The counts it
prints describe that corpus only; they are not a recall figure for regex
detection in general.

Redaction runs the most specific patterns first (card, SSN, IP, email, then
phone). The first edition ran the phone pattern before the card pattern, which
splits a card number; the self-test reproduces that.

## Running

```bash
dotnet run -- --self-test   # offline: labelled corpus, RESULT n/n
dotnet run                  # parts 1-2 offline, part 3 calls the configured model
```
