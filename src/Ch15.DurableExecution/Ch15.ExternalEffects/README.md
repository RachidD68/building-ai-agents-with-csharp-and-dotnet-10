# Ch15.ExternalEffects

A workflow step that pays, and the windows around it. `Ch08.UncertainOutcome` reconciled one uncertain provider call; inside a workflow the checkpoint lags the effect, so there are more windows. Its simulated provider (`FakePaymentProvider`, with its own journal file) and reconciler (`DeliveryWorker`) are used here unchanged, through a project reference.

```
authorize --> payout --> settle
          ^ restart point: the checkpoint after authorize holds the order for the payout step

payout:  business record -> provider call -> provider journal -> local record -> (next checkpoint)
                         ^ before-effect   ^ after-accept      ^ after-record
```

Three durable states, three owners, three files under `--store`: `checkpoints.db` (the framework's checkpoints, `SqliteCheckpointStore`), `app.db` (the business record: payouts, leases and Ch08's deliveries) and `provider.db` (the provider's journal; every row is one effect).

A worker that dies anywhere in the payout step restarts from the checkpoint taken before it, so the step runs again. It is safe to run again because it:

- re-reads the business record first: `Paid` stays `Paid`, with no provider call;
- uses the operation id issued at approval, never a new one (the provider reference is `contoso:<operation id>`);
- reconciles an attempt Ch08's delivery worker left `Sending` or `OutcomeUnknown` instead of sending it blind;
- commits only under the current lease: every write repeats the fencing-token check in its own statement.

Cancelling is not undoing: a cancel before the commit, while the payout is still `Approved`, stops it (`Cancelled`); a cancel after it leaves `Paid` and says that a refund would be a new, approved operation; a cancel during a call whose reply is lost leaves `Unknown`, and reconciliation finds the payment.

`SqliteCheckpointStore` (a `JsonCheckpointStore` over its own SQLite file) stands in for a shared production store: unlike `FileSystemJsonCheckpointStore`, it lets several workers open it at once, so only the lease and its fencing token keep two of them apart. The run uses `InProcessExecution.Lockstep`, so the restart point is stored before the payout step starts.

## Running

```bash
dotnet run --project src/Ch15.DurableExecution/Ch15.ExternalEffects -- start CLM-2001
dotnet run --project src/Ch15.DurableExecution/Ch15.ExternalEffects -- start CLM-2001 --pause-at after-accept   # close the window while it waits
dotnet run --project src/Ch15.DurableExecution/Ch15.ExternalEffects -- resume CLM-2001
dotnet run --project src/Ch15.DurableExecution/Ch15.ExternalEffects -- status CLM-2001
dotnet run --project src/Ch15.DurableExecution/Ch15.ExternalEffects -- --self-test
```

Verbs: `approve`, `start`, `resume`, `cancel`, `status`. Options: `--store <dir>`, `--pause-at before-commit|before-effect|after-accept|after-record` (prints `PAUSED at <point>` and waits for a line on stdin), `--fault accept-then-timeout|lost-request`, `--lease-seconds <n>` (default 30), `--owner <name>`, `--naive` (the step with a fresh operation id per attempt and no record check: the defect). Exit codes: 0 Paid or Cancelled, 4 refused (stale lease, conflict), 5 nothing done, 6 Unknown.

The self-test runs every window in process (a replay is a second run from the restart point) and prints `RESULT n/n`. The proof with real processes killed at each point is `Ch15.RestartTests`. No model is involved.
