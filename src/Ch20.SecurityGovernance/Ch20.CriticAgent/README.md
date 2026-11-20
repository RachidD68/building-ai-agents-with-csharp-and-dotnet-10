# Ch20.CriticAgent: a second model as a scored, fallible signal

A writer agent answers; a critic agent returns a `ValidationResult` through
`RunAsync<ValidationResult>()`; `CriticGate` decides, comparing the critic with the
deterministic PII detector (linked from `Ch20.InputSanitization`):

| Situation | Decision |
|---|---|
| detector finds PII | Reject (`pii_detector`) |
| critic gives no usable verdict (prose, bad JSON) | Reject (`critic_unavailable`) |
| score outside 1-10 | Reject (`critic_out_of_range`) |
| critic sees PII, detector does not | Escalate |
| `passed` disagrees with the score | Escalate |
| otherwise | Publish when score >= 7, else Reject |

The critic judges text. A well-written claim that a reimbursement for another
tenant's employee was submitted passes it; that is the policy's job.

## Running

```bash
dotnet run -- --self-test   # offline: scripted writer and critic, RESULT n/n
dotnet run                  # live, against the configured provider
```
