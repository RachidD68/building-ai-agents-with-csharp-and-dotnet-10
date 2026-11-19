# Ch08.UncertainOutcome

The local commit is done and the outbox says "pay the employee". The worker calls the payment provider; the provider accepts the payment, and the response is lost. Did the money move? The worker cannot know, so it records exactly that: `OutcomeUnknown`.

A later run, in another process, calls `DeliveryWorker.ResumeAsync`:

- it asks the provider for the payment by the stable reference (the operation id). Found: `Reconciled`.
- not found: it sends the request again with the **same** reference, which the provider deduplicates: `Delivered`.
- still a timeout: it stays `OutcomeUnknown` for the next resume.

The delivery state is written before the call (`Pending` -> `Sending` in one conditional update), so a worker that dies during the call leaves a `Sending` row that the next run reconciles, and a second worker never sends it again.

The provider is `FakePaymentProvider`: a simulated provider with its own journal (a second SQLite file), so a second process sees what the first one paid. Each journal row is one simulated effect. Nothing here moves money, and "exactly once" is not promised for an arbitrary provider: it holds here because this provider deduplicates on the reference.

## Running

```bash
dotnet run --project src/Ch08.AuthorizedActions/Ch08.UncertainOutcome                     # both steps in one process
dotnet run --project src/Ch08.AuthorizedActions/Ch08.UncertainOutcome -- submit --store <dir> --fault accept-then-timeout
dotnet run --project src/Ch08.AuthorizedActions/Ch08.UncertainOutcome -- resume --store <dir>
dotnet run --project src/Ch08.AuthorizedActions/Ch08.UncertainOutcome -- --self-test
```

Faults: `accept-then-timeout`, `lost-request`, `crash-after-accept`. The self-test runs `submit` and `resume` as separate child processes for each fault, checks one effect and one operation id each time, and reproduces the defect it replaces: a blind retry with a new reference pays twice.
