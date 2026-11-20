# Contoso v6: API and interface

The Contoso expense assistant behind an HTTP API, with an interface that shows
the state of the work. The hosting chapter builds the API half; the interface
chapter adds the interface half to the same project.

```
token (tid, sub) -> JWT bearer -> identity key -> authorization, quota, conversation store

API half
GET  /claims/{id}            v5's claim, for its requester or a finance reviewer of the tenant; else 404
POST /claims/{id}/decision   the caller is the reviewer; v5's rules: separation_of_duties,
                             reviewer_not_authorized, not_awaiting_review, already_decided

interface half
GET  /claims/{id}/events     the claim's operation state as typed SSE (Ch18.SseStatusStream),
                             resumable with Last-Event-ID; same visibility rule (else 404)
GET  /claims/{id}/snapshot   the same view at the current cursor
GET  /claims/{id}/review     finance reviewers: amount, currency, account version, digest
POST /claims/{id}/approval   {"accountVersion":8,"digest":"...","decision":"approve"|"reject"}
                             403 reviewer_not_authorized / separation_of_duties, 409 stale_version /
                             digest_mismatch / not_awaiting_review / already_decided; a repeat of the
                             stored decision is answered from it: 200 "approved (replayed)"

v5 worker (separate process, same store): prepare, resume
```

What v6 adds to v5: nothing in v5 changes. v6 references v5 (DurableClaims,
the v2 desk) and the hosting chapter's `Ch17.AspNetCoreHost` (its whole
composition: bearer validation with `MapInboundClaims = false`, owned
conversations, the (tenant, subject) quota, one retry owner, liveness and
readiness), and maps the claim routes over v5's store. The identity system's
answer is v5's `identity_rights` table, read on every request, so a revoked
role is gone at once.

The reviewer of a decision is the token's subject. The request body carries
only the decision (and, on `/approval`, the account version and digest the
reviewer saw); a `reviewer` field in the JSON is never read.

The interface's operation states come from v5's claim states, never from
text: AwaitingReview is AwaitingApproval (or Running once decided, Expired
when the decision expired), Committed is Running until delivery, Delivered and
Reconciled are Completed, OutcomeUnknown stays OutcomeUnknown, Rejected is
Failed. A background loop (`ClaimFeed`) polls the claims a client has opened,
because v5's worker runs in another process.

## What this snapshot does not do

- Claims are still created and paid by v5's worker (`prepare`, `resume`): the
  API reads and decides, it does not run the workflow.
- v5's identities are one tenant (`contoso`); a `fabrikam` token is valid but
  holds no rights there, so it sees nothing.
- The event log is in memory. A restarted API starts new runs and cursors;
  a client then reloads the snapshot.
- No AG-UI route: the only cached AG-UI hosting build needs
  `Microsoft.Agents.AI.Hosting` 1.5, and this host runs 1.28 (see
  `src/Ch18.StreamingInterfaces/Ch18.AguiServer`). The SSE status stream is
  the GA path.
- No revise: v5 has no proposal versions beyond (amount, account version).
  `Ch18.ApprovalUi` shows revise.
- The build output's `appsettings.json` is v5's (a referenced project's content
  file); the model settings fall back to AgenticAI.Common's defaults (Ollama,
  llama3.2) unless set in user secrets or the environment.

## Running

```bash
dotnet run --project contoso/v6-api-ui -- --self-test
dotnet run --project contoso/v5-durable-resume -- prepare
cd contoso/v6-api-ui
dotnet user-jwts create --name rita --claim tid=contoso --output token
dotnet run
curl http://localhost:5000/claims/<id> -H "Authorization: Bearer <token>"
curl -N http://localhost:5000/claims/<id>/events -H "Authorization: Bearer <token>"
```

The self-test runs v5's `prepare` and `resume` as child processes and the API
on an in-memory TestServer between them, with minted tokens and a fake model:
sections 1 to 4 through the API half, section 5 through the interface half
(a second claim's events, review, a double-clicked approval, a resume while
the client follows the stream). Simulated effects only.
