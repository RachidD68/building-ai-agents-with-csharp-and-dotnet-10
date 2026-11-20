# Ch20.PolicyEnforcement: one decision at the effect, on every transport

`ReimbursementService.Reimburse` is the only code that moves a balance. Every
transport ends there with a `Caller` it built from its own authentication
context:

| Transport | How the test reaches it | Caller from |
|---|---|---|
| HTTP API | Kestrel on 127.0.0.1, `POST /reimbursements` | bearer token (in-memory token table) |
| MCP tool | `ModelContextProtocol` server over in-memory pipes (the stdio wiring) | the user bound when the server starts |
| A2A host | the agent run the host makes (`RunAsync`) | the host's auth context |
| AG-UI host | the agent run the host makes (`RunStreamingAsync`) | the host's auth context |

The A2A and AG-UI hosting packages are preview, so the test drives the agent
run those hosts would make instead of hosting them. Every request also sends a
`tenantId` argument; nothing reads it.

The decision, in order: tool revoked, subject revoked, account lookup (an
unknown employee gets the same answer as another tenant's), Ch08's
`ReimbursementPolicy` (permission, scope, amount, currency), duplicate
operation, then the added rules. Added rules have a mode: `daily_limit` starts
in **Observe** (records "would deny", lets the request through) and is switched
to **Enforce** once measured. Baseline rules have no mode. The decision and the
debit run under the account's lock.

The self-test also reproduces two shapes that fail: an MCP tool that calls the
ledger directly (the check lived in the API only), and check-await-debit under
10 concurrent claims.

## Running

```bash
dotnet run -- --self-test   # no model, loopback only: RESULT n/n
```
