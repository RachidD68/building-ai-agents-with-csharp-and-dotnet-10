# Ch19.IncidentDiagnosis

Which step gave the wrong reimbursement limit: retrieval, the limit tool, or the model?

Three injected faults end in the same kind of confident, wrong answer:

| Fault | What happens | Signal that names it |
|---|---|---|
| `EmptyRetrieval` | `search_policy` finds nothing, so the model looks up the wrong category | `contoso.retrieval` span, `contoso.retrieval.hits=0` |
| `ToolException` | `get_reimbursement_limit` throws, so the model guesses | `execute_tool get_reimbursement_limit` span with an error |
| `WrongValue` | the tool is right, the model misquotes it | `contoso.answer.check` span, `contoso.answer.reason=amount_mismatch` |

With sensitive data off (as it should be), the framework spans alone show `EmptyRetrieval`
and `WrongValue` as two successful runs. The two step spans carry counts and verdicts, never
document text, prompts or tool results.

## Running

```bash
dotnet run                                   # the three faults, diagnosed
dotnet run -- --capture wrongvalue run.jsonl # one request, exported spans to a file
dotnet run -- --diagnose run.jsonl           # name the failing step from the file
dotnet run -- --self-test                    # offline checks, RESULT n/n
```

`--diagnose` reads this runner's own JSONL (one `SpanRecord` per line, as `--capture` writes it).
A trace exported from a backend (OTLP/JSON, a vendor download) has to be converted to that format
first. The verdict on the answer check comes from `contoso.answer.reason`, not from how an
exporter spelled the `contoso.answer.grounded` boolean.

Everything is offline: a scripted model, no provider, no network.

The challenge exercise is this project's `--capture` / `--diagnose` pair: given a captured
regression, name the responsible step, and show that a search of everything exported finds
none of the seeded confidential strings (`--self-test` does both).
