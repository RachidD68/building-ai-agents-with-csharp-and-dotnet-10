# Ch17.A2APreview — an agent over A2A, behind a token

One agent registered with `builder.AddAIAgent(...).AddA2AServer()` and mapped
with both A2A bindings, JSON-RPC (`MapA2AJsonRpc`) and HTTP+JSON
(`MapA2AHttpJson`), at `/a2a/expense-assistant`. Both bindings require a
bearer token. The agent card (`MapWellKnownAgentCard`, from the A2A SDK) is
public at `/.well-known/agent-card.json` and declares the bearer scheme.

Message mode only: the agent answers with a message. The A2A hosting of
MAF 1.28 has no run-mode option on `A2AServerRegistrationOptions`; the SDK
still maps its task routes (`/tasks`, `/tasks/{id}`...), and they sit behind
the same token.

**Preview.** `Microsoft.Agents.AI.Hosting.A2A` and `.A2A.AspNetCore` are
1.28.0-preview, the A2A SDK is 1.0.0-preview, and `AddA2AServer` and the
`MapA2A*` methods are experimental (MEAI001). The GA alternative is a plain
minimal-API route, as in `Ch17.AspNetCoreHost`.

## Run

```bash
dotnet run --project src/Ch17.HostingIdentity/Ch17.A2APreview -- --self-test
```

The self-test starts the app on an in-memory TestServer with a fake model,
prints the routes the A2A hosting maps, reads the card with `A2ACardResolver`,
and sends one message over each binding with `A2AClient` and
`A2AHttpJsonClient`, with and without a token. No model, no network.
