# Ch20.ThreatRegisterTests: the threat register as data, one test per row

`threat-register.json` lists each threat with its OWASP ids (LLM Top 10 2025,
Agentic Top 10 ASI01-ASI10), the control, the test, the owner, the residual
risk and the stop procedure.

The self-test:

1. checks the register (sequential ids, an owner, a residual risk and a stop
   procedure on every row, the OWASP id format, one row per test);
2. prints the detection matrix for a politely worded request to reimburse
   another tenant's employee: keyword filter, injection detector, PII
   detector, retrieval scanner and critic gate all say "safe"; the policy at
   the effect denies it;
3. runs each row's test with every control on (it must pass) and again with
   that row's control removed (it must fail).

The tests run the chapter's own controls: their source files are linked from
the other Ch20 projects, not copied.

`--live` runs a small injection suite (direct, harmless wording, indirect,
plus a benign control) against the configured model. The model's choices are
reported; the asserted invariant is that no forbidden effect happens.

## Running

```bash
dotnet run -- --self-test   # offline: RESULT n/n
dotnet run -- --live        # against the configured model (Ollama llama3.2 by default)
```
