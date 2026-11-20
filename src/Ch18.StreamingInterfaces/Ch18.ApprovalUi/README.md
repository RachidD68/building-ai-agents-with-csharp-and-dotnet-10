# Ch18.ApprovalUi: approval as a reviewable decision

The reviewer approves exact arguments, not a sentence. The screen
(`GET /reviews/{id}`) shows the amount, currency, account version, the
proposal's version and the digest that binds them (ch. 8's
`ReimbursementPolicy.Digest`). The POST names only what the reviewer saw:

```json
{ "version": 1, "digest": "80DC9D5A...", "decision": "approve", "note": "optional" }
```

`decision` is `approve`, `reject` or `revise`. The server loads the proposal
itself and checks, in order:

| check | refusal |
|---|---|
| the proposal exists in the caller's tenant | 404 `not_found` |
| the caller is a finance reviewer now (rights asked on every call) | 403 `reviewer_not_authorized` |
| the caller is not the requester | 403 `separation_of_duties` |
| the version is the latest | 409 `stale_version` |
| the digest matches that version | 409 `digest_mismatch` |
| a decision is already stored for that version | 200 replayed (same reviewer, same decision), else 409 `already_decided` |
| the proposal has not expired (24 h) | 410 `expired` |

One decision per (proposal, version): the primary key decides a double click.
`reject` ends the proposal; `revise` asks for a new version, and the old
version's approval can no longer be used. The commit (`ApprovalDesk.Commit`)
runs ch. 8's `ReimbursementPolicy.Evaluate` with the approval loaded from the
store, then ch. 8's `OperationStore.Submit` (commit once, replay after).

## Run

```bash
dotnet run --project src/Ch18.StreamingInterfaces/Ch18.ApprovalUi -- --self-test
dotnet run --project src/Ch18.StreamingInterfaces/Ch18.ApprovalUi
```

Offline, a temporary SQLite file. The self-test reproduces the double click
with a check-then-insert table (two approvals stored) and an approval keyed by
proposal id alone (it covers a raised amount), then shows the rules above.
Simulated effects only.
