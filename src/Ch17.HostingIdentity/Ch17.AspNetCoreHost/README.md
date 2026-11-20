# Ch17.AspNetCoreHost — one agent behind a validated identity

The hosting chapter's minimal solution: an agent registered with
`builder.AddAIAgent` (Microsoft.Agents.AI.Hosting, preview) behind an ASP.NET
Core API in which every request carries a validated identity.

## The identity key

`CallerIdentity.From(user)` reads `tid` and `sub` from the principal the JWT
bearer handler validated (signature, issuer, audience, lifetime), with
`MapInboundClaims = false` so the names stay as they are in the token. The
(tenant, subject) key feeds three consumers:

- **authorization**: a conversational route without a key answers 403;
- **the rate limit**: one partition per (tenant, subject), never `sub` alone
  and never the IP for a signed-in caller;
- **the conversation store**: the conversations chapter's `SqliteSessionStore`
  (linked from `Ch06.SessionOwnership`), whose key carries both partitions.

## Routes

| Method | Path | Kind |
|---|---|---|
| POST | `/ask` | SINGLE-TURN: a fresh session per request, nothing stored |
| POST | `/conversations` | issues an id owned by the caller |
| POST | `/conversations/{id}/messages` | CONVERSATIONAL: load authorized, run, save versioned (409 on a lost race) |
| DELETE | `/conversations/{id}` | owner only; a no-op for anyone else |
| GET | `/agents`, `/health/live`, `/health/ready` | anonymous; no health probe calls the model |

One retry owner: `AddLlmChatClient` (AgenticAI.Common) puts one resilience
handler on the provider's HttpClient and turns the SDK's retries off.

## Run

```bash
dotnet run --project src/Ch17.HostingIdentity/Ch17.AspNetCoreHost -- --self-test
dotnet run --project src/Ch17.HostingIdentity/Ch17.IdentityTests

# A real run (Ollama llama3.2 by default). user-jwts writes the signing key to
# user secrets and the issuer and audiences to appsettings.Development.json.
cd src/Ch17.HostingIdentity/Ch17.AspNetCoreHost
dotnet user-jwts create --name alice --claim tid=contoso --output token
dotnet run --launch-profile Ch17.AspNetCoreHost
curl -X POST http://localhost:56400/conversations -H "Authorization: Bearer <token>"
curl -X POST http://localhost:56400/conversations/<id>/messages -H "Authorization: Bearer <token>" \
     -H "Content-Type: application/json" -d '{"message":"What can I claim for a taxi?"}'
```

The self-test covers the identity key, the partitions (with the first
edition's key as the reproduced defect) and the retry count. The two-user
proof over HTTP is `Ch17.IdentityTests`.

`--self-test` and `Ch17.IdentityTests` need no model and no network.
