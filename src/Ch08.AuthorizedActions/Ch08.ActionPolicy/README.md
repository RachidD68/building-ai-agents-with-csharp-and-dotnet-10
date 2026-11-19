# Ch08.ActionPolicy

A deterministic policy that answers one question before anything is committed: may this authenticated caller commit this exact reimbursement against this account, as it is now?

`ReimbursementPolicy.Evaluate` returns `Deny`, `RequireApproval` or `Allow`, each with a reason code, and checks in this order:

permission -> scope (tenant, employee) -> operation id -> amount (`> 0` and `<=` reimbursable balance) -> currency -> version -> low-value `Allow` -> approval

An approval counts only when it is approved, was given by someone with the `finance-reviewer` role who is not the requester, covers the same tenant, requester and policy version, has not expired, and carries the digest of these exact values.

## What it fixes compared with the draft prototype

| Gap | Fix |
|---|---|
| Digest over the serialized decimal: `100m` and `100.00m` differed | Canonical form: invariant culture, no trailing zeros, a JSON array of strings, SHA-256 |
| Approval bound to the requester, no reviewer | `ApprovalRecord` names the requester and the reviewer and the reviewer's role; the reviewer may not be the requester |
| Currency compared as typed | Trimmed, upper case, three ASCII letters; anything else is refused |
| No revocation case | Permissions are reloaded for every evaluation; a right revoked between proposal and commit is refused at commit |
| No `--self-test` convention | `RESULT n/n`, exit code 1 on any failure |

The digest binds values. It does not prove who approved them: load `ApprovalRecord` from a server-side store written after the reviewer was authenticated, never from the model or the client.

The suite also keeps the first-edition counterexamples as labelled defect reproductions: the substring grounding check, and the `amount <= 50` auto-approve rule that accepts -10 and 0. They pass when they show the defect.

## Running

```bash
dotnet run --project src/Ch08.AuthorizedActions/Ch08.ActionPolicy -- --self-test
```

No model, no package, no API key.
