# Ch18.AguiServer: the Contoso assistant over AG-UI

| route | what |
|---|---|
| `POST /agui/assistant` | AG-UI (`MapAGUI`), policy `employee`: submit a claim, list own claims |
| `POST /agui/review` | AG-UI (`MapAGUI`), policy `finance-reviewer`: pending reviews |
| `GET /conversations/{thread}/events`, `/snapshot` | the operation's state (Ch18.SseStatusStream), thread owner only |
| `GET /reviews/{id}`, `POST /reviews/{id}/decision` | the review screen (Ch18.ApprovalUi) |

- **Selection after authorization.** The route chooses the agent, and the
  route's policy reads the caller's rights on every request, so a revoked
  reviewer loses the review agent at once.
- **The thread id is a locator.** AG-UI's `threadId` comes from the client.
  With a session store registered, `MapAGUI` finds the session by that id
  alone: the self-test reproduces another user reading the thread. This host
  binds each thread id to the first validated (tid, sub) that uses it; anyone
  else gets 404.
- **One agent instance.** `MapAGUI` 1.5 builds the agent once, when the route
  is mapped, even when it is registered Transient (the self-test counts it).
  So the tools hold no identity: each call reads the caller from the request.
- **The application sets the state.** `submit_claim` proposes; the claim is
  AwaitingApproval above 50 EUR and Completed only after the commit. The
  response's `RUN_FINISHED` is not the end of the operation.

**Preview.** `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore` 1.5.0-preview is
the only AG-UI build in the offline cache. It is compiled against
`Microsoft.Agents.AI.Hosting` 1.5: with Hosting 1.28, `MapAGUI` throws
`TypeLoadException` (`AgentSessionStore` moved). This project overrides
Hosting to 1.5.0-preview, so it cannot share a process with Contoso v6
(Hosting 1.28). The GA alternative is the status stream alone, which v6 maps.

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
