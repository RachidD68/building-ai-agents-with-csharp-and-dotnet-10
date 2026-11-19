# Exercise 03 — Full Safety Pipeline (Challenge)

## Prompt

Wire all five Quality Pyramid layers on the Contoso FAQ agent: input sanitization
(both regex and Azure AI Content Safety), middleware (logging, timing, content
filter, audit), output filters (PII redaction + profanity), critic agent with a
`ValidationResult` record, and a human-review escalation path. Then extend the
adversarial set to cover the new attack classes: at least one indirect-injection
prompt (payload inside a retrieved doc), one Excessive-Agency attempt (trick the
agent into calling the destructive tool), one improper-output-handling case (an
answer carrying an injection vector), and one unbounded-consumption case (a loop
trigger). Run the full set (safe + adversarial) and produce a matrix showing which
layer — including the new layers — caught each one.

## Key Concepts

- The five Quality Pyramid layers: input sanitization, middleware, output filters,
  critic agent, human review — composed on one SafeAgent
- Azure AI Content Safety is config-gated (`ContentSafety:Endpoint`); the regex
  detectors always run, so the exercise works without Azure
- Indirect injection: retrieved FAQ docs are scanned like any input; a poisoned doc
  is dropped before it can steer the model
- Tool gate: the agent holds `delete_customer_record` but the gate refuses
  unattended execution (Excessive Agency); `next_step` + `MaximumIterationsPerRequest`
  and a token budget cover unbounded consumption
- Output filters treat the answer as untrusted: PII redaction, profanity masking,
  and HTML-encoding of injection vectors (improper output handling)
- Escalation path: critic rejection or repeated violations flag the run for a human
- The 20-prompt matrix at the end attributes every prompt (benign + adversarial) to
  the layer that caught it, with a pass/fail summary

## Run

`dotnet run` — needs a configured model (Ollama by default). Input-layer catches are
deterministic; tool-driven rows (excessive agency, loop) depend on the model actually
taking the bait, and the matrix reports what really happened.
