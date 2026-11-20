# Contoso v6: API and interface

The Contoso expense assistant behind an HTTP API. The hosting chapter builds
the API half (this version of the folder); the next chapter adds the interface
to the same project.

```
token (tid, sub) -> JWT bearer -> identity key -> authorization, quota, conversation store
GET  /claims/{id}            v5's claim, for its requester or a finance reviewer of the tenant; else 404
POST /claims/{id}/decision   the caller is the reviewer; v5's rules: separation_of_duties,
                             reviewer_not_authorized, not_awaiting_review, already_decided
v5 worker (separate process, same store): prepare, resume
```

What v6 adds to v5: nothing in v5 changes. v6 references v5 (DurableClaims,
the v2 desk) and the hosting chapter's `Ch17.AspNetCoreHost` (its whole
composition: bearer validation with `MapInboundClaims = false`, owned
conversations, the (tenant, subject) quota, one retry owner, liveness and
readiness), and maps two claim routes over v5's store. The identity system's
answer is v5's `identity_rights` table, read on every request, so a revoked
role is gone at once.

The reviewer of a decision is the token's subject. The request body carries
only the decision; a `reviewer` field in the JSON is never read.

## What this snapshot does not do

- Claims are still created and paid by v5's worker (`prepare`, `resume`): the
  API reads and decides, it does not run the workflow.
- v5's identities are one tenant (`contoso`); a `fabrikam` token is valid but
  holds no rights there, so it sees nothing.
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
```

The self-test runs v5's `prepare` and `resume` as child processes and the API
on an in-memory TestServer between them, with minted tokens and a fake model.
Simulated effects only.
