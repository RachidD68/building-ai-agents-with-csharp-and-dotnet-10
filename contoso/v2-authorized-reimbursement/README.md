# Contoso v2: authorized reimbursement

The Contoso expense assistant at the end of the authorized-actions chapter. An employee files a claim; the model proposes a reimbursement; the application decides and commits it once.

```
claim -> model proposal (structured output) -> ActionPolicy
      -> Deny | AwaitingReview -> reviewer (not the requester) | Allow
      -> IdempotentCommit (unique operation id, version recheck, outbox)
      -> payment provider -> OutcomeUnknown -> reconciliation
```

`ReimbursementDesk` composes the chapter's three projects as they are: `Ch08.ActionPolicy`, `Ch08.IdempotentCommit` and `Ch08.UncertainOutcome`. Identity comes from the caller the host authenticated, never from the model. The claim's submission id, created once when the form opens, is the operation id from start to end.

The scenario: Alice's taxi claim (64.80 EUR) needs review, and she double-clicks Submit; Alice, who also holds the finance-reviewer role, raises it to 450.00 and tries to approve it herself, which is refused and stores nothing; Rita edits the amount to 60.00 and approves, and the approval arrives twice at the same moment (two threads); the provider accepts the payment and the response is lost; the process restarts and reconciles. Then a low-value claim goes straight through, a claim whose text asks to pay another employee is denied, and a claim whose requester loses the right to submit while it waits for review is denied at commit.

The reviewer's role and tenant come from the authenticated caller (`PermissionDirectory` stands in for the host's identity system), never from a parameter the client sets: the claim is looked up in the reviewer's own tenant, and the role, separation of duties and the amount the reviewer was shown are checked directly, for every amount, before anything is stored. A review sent from a screen whose amount is no longer the claim's is refused (`proposal_changed`) and stores nothing. An edited amount must pass the policy before it is stored; the first valid decision claims the approval row and stores the reviewed amount in the same transaction; a claim's state only moves forward, so a duplicate approval can never mark a paid claim Rejected. Intake, the proposal, the review, the commit and the claim's state are separate transactions; after a restart, `ResumeAsync` first repairs a claim a crash left between them: an operation that committed marks the claim Committed; a stored approval with no operation is committed now, after the reviewer's role is read again from the directory and the policy runs again; a Submitted claim with a stored proposal is decided now; and a Submitted claim without one becomes Rejected (`proposal_failed`), which a resubmit retries. A model that cannot be reached is recorded the same way. Three loops take one item at a time: the claim repair, the outbox dispatch and the delivery worker's reconciliation. An error in one item, other than the caller's cancellation, is logged to stderr and leaves that item as it was for the next restart, and the loop goes on with the others. A provider that refuses a payee outright therefore leaves that payment `OutcomeUnknown` for a person, while the other unknown payments still settle. A stored approval whose account no longer exists ends Rejected (`resource_scope`). Every commit moves the account version, so one employee's claims meet: a review is bound to the version current when the reviewer decides, a low-value claim that loses the race to another commit is rebuilt on the new version and decided again, and an approval whose version moved before its commit sends the claim back to the reviewer (`AwaitingReview`, `account_changed`) with the model's proposed amount: an earlier reviewer's edit was never approved at the new version, so it is dropped. The store's refusals reach the claim as reason codes (`stale_version`, `insufficient_balance`, `operation_conflict`, `unknown_account`). The self-test crashes after intake, after the proposal, after the review and after the commit, simulates a model outage, revokes the reviewer's role between the review and the restart, has a second tenant reuse an operation id (the payment reference is `<tenant>:<operation id>`), pays a second claim of the same employee while the first waits for review, runs six of one employee's claims at once, re-reviews an edited approval after the version moved (the edit is dropped, and a review of the old screen is refused), deletes an account under a stranded approval next to an unknown payment, has the provider refuse one payee, and gives a stranded claim an amount the restart cannot read.

Claim states: `Submitted`, `AwaitingReview`, `Rejected`, `Committed`, `Delivered`, `OutcomeUnknown`, `Reconciled`.

## What this snapshot does not do

- One desk per database. The dispatcher is serialized inside one process only; two instances need a lease or owner column on the outbox and delivery rows, or a commit may briefly read another instance's `Sending` row as `OutcomeUnknown`.
- Status lookups are not scoped to the requester. `ReviewAsync` and a resubmitted `SubmitAsync` return a claim's state and payment id to any caller of the tenant who has its operation id; a real host checks the requester or the reviewer role first.
- No provider error taxonomy. Only a timeout means "unknown"; any other provider error leaves the delivery `Sending` (shown as `OutcomeUnknown`) for the next resume and a person, instead of classifying it as a permanent refusal or a retryable fault.

## Why a console app

It is the smallest host that shows the whole path with real SQLite state and a restart. The API and interface snapshot adds ASP.NET Core and an authenticated caller later; here the callers are constructed as the host's authentication would produce them.

## Running

```bash
dotnet run --project contoso/v2-authorized-reimbursement                  # Ollama llama3.2 by default
dotnet run --project contoso/v2-authorized-reimbursement -- --self-test   # scripted model, no network
```

Simulated effects only: the payment provider is a fake with its own journal file, and both databases live in a temporary folder that is deleted at the end.

## Earlier snapshots

This snapshot is self-contained and does not reference `v0-agent-fake-model` or `v1-owned-conversation`. It keeps v1's tenant and employees (contoso, E-1001, E-2002) and its rule that identity comes from authentication, never from the payload. It builds its own claims store rather than v1's session store, and it defines the minimal `ExpenseClaim` it needs instead of the typed-responses chapter's full version.
