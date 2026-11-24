# Ch18.AguiServer: the Contoso assistant over AG-UI

| route | what |
|---|---|
| `POST /agui/assistant` | AG-UI (`MapAGUIServer`), policy `employee`: submit a claim, list own claims |
| `POST /agui/review` | AG-UI (`MapAGUIServer`), policy `finance-reviewer`: pending reviews |
| `GET /conversations/{thread}/events`, `/snapshot` | the operation's state (Ch18.SseStatusStream), thread owner only |
| `GET /reviews/{id}`, `POST /reviews/{id}/decision` | the review screen (Ch18.ApprovalUi) |

- **Selection after authorization.** The route chooses the agent, and the
  route's policy reads the caller's rights on every request, so a revoked
  reviewer loses the review agent at once.
- **The thread id is a locator.** AG-UI's `threadId` comes from the client.
  With a session store that is not isolated, `MapAGUIServer` finds the
  session by that id alone: the self-test reproduces another user reading the
  thread. Hosting 1.28's `WithInMemorySessionStore()` isolates by default and
  needs an `AgentIsolationKeyProvider` (without one, the run fails); this
  host's provider returns the validated (tid, sub), so another caller on the
  same thread id gets a session of their own. The thread id also names the
  operation's status stream, so the host still binds each thread id to the
  first validated (tid, sub) that uses it; anyone else gets 404. The guard reads the id the way MAF does: MAF's parser matches
  `threadId` case-insensitively and keeps the last duplicate, so a body with a
  second `ThreadId`/`THREADID`, or a lone one not spelled `threadId`, gets 400.
  Production gap: a claimed id is never released or expired, so thread ids
  should be unguessable (or issued by the server) and expire.
- **An agent instance per request.** `MapAGUIServer` 1.28 builds the agent,
  registered Transient, for each AG-UI request that reaches it (the self-test
  counts them). The tools hold no identity either way: each call reads the
  caller from the request.
- **The application sets the state.** `submit_claim` proposes; the claim is
  AwaitingApproval above 50 EUR and Completed only after the commit. The
  response's `RUN_FINISHED` is not the end of the operation.

**Preview.** `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore` 1.28.0-preview is
on the same line as `Microsoft.Agents.AI.Hosting` 1.28.0-preview, so no
version override is needed. Its API is `AddAGUIServer()` and
`MapAGUIServer(...)` (1.5's `AddAGUI`/`MapAGUI` are gone). The client package,
`Microsoft.Agents.AI.AGUI`, has no 1.28 build; `Ch18.AguiClient` runs
1.13.0-preview against this host. The GA alternative is the status stream
alone, which Contoso v6 maps.

## Run

```bash
dotnet run --project src/Ch18.StreamingInterfaces/Ch18.AguiServer -- --self-test
cd src/Ch18.StreamingInterfaces/Ch18.AguiServer
dotnet user-jwts create --name E-1001 --claim tid=contoso
dotnet run
```

The self-test uses a fake model, an in-memory TestServer and tokens minted in
process. The live run with a real model is `Ch18.AguiClient`'s `dotnet run`.
Simulated effects only.
