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

The reviewer's role and tenant come from the authenticated caller (`PermissionDirectory` stands in for the host's identity system), never from a parameter the client sets: the claim is looked up in the reviewer's own tenant, and the role and separation of duties are checked directly, for every amount, before anything is stored. An edited amount must pass the policy before it is stored; the first valid decision claims the approval row and stores the reviewed amount in the same transaction; a claim's state only moves forward, so a duplicate approval can never mark a paid claim Rejected. Intake, the proposal, the review, the commit and the claim's state are separate transactions; after a restart, `ResumeAsync` first repairs a claim a crash left between them: an operation that committed marks the claim Committed; a stored approval with no operation is committed now, after the reviewer's role is read again from the directory and the policy runs again; a Submitted claim with a stored proposal is decided now; and a Submitted claim without one becomes Rejected (`proposal_failed`), which a resubmit retries. A model that cannot be reached is recorded the same way. Each stranded claim is repaired on its own, so one that fails is logged and left for the next restart while the outbox and the unknown payments are still settled; a stored approval whose account no longer exists ends Rejected (`resource_scope`). Every commit moves the account version, so one employee's claims meet: a review is bound to the version current when the reviewer decides, a low-value claim that loses the race to another commit is rebuilt on the new version and decided again, and an approval whose version moved before its commit sends the claim back to the reviewer (`AwaitingReview`, `account_changed`). The store's refusals reach the claim as reason codes (`stale_version`, `insufficient_balance`, `operation_conflict`, `unknown_account`). The self-test crashes after intake, after the proposal, after the review and after the commit, simulates a model outage, revokes the reviewer's role between the review and the restart, has a second tenant reuse an operation id (the payment reference is `<tenant>:<operation id>`), pays a second claim of the same employee while the first waits for review, runs six of one employee's claims at once, and deletes an account under a stranded approval next to an unknown payment.

Claim states: `Submitted`, `AwaitingReview`, `Rejected`, `Committed`, `Delivered`, `OutcomeUnknown`, `Reconciled`.

## Why a console app

It is the smallest host that shows the whole path with real SQLite state and a restart. The API and interface snapshot adds ASP.NET Core and an authenticated caller later; here the callers are constructed as the host's authentication would produce them.

## Running

```bash
dotnet run --project contoso/v2-authorized-reimbursement                  # Ollama llama3.2 by default
dotnet run --project contoso/v2-authorized-reimbursement -- --self-test   # scripted model, no network
```

Simulated effects only: the payment provider is a fake with its own journal file, and both databases live in a temporary folder that is deleted at the end.

## Earlier snapshots

This snapshot is self-contained. The earlier snapshots (`v0-agent-fake-model`, `v1-owned-conversation`) are not in the repository yet. When they exist, v2 should take from them: the agent and fake-model test setup (v0), the owned conversation and session store (v1), and the typed `ExpenseClaim` with domain validation from the typed-responses chapter, which v2 defines here in its smallest form.
