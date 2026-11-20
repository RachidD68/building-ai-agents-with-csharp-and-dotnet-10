# Ch24.KillSwitch: withdraw one capability, keep the conversations

During an incident Contoso withdraws the `reimburse` tool while the read-only
FAQ (`search_faq`) stays up. The switch is code, read on every call:

| Where | What checks the registry | Answer while withdrawn |
|---|---|---|
| Tool | function-invocation middleware (`AIAgentBuilder.Use` with a `FunctionInvocationContext` callback) | a refusal as the tool result; the function never runs |
| HTTP boundary | an endpoint filter on `POST /reimbursements` | `503` problem `capability_withdrawn` |
| The effect | the security chapter's `ReimbursementService` (`Ch20.PolicyEnforcement`), fed by the registry's propagator | `Deny tool_revoked` |

`CapabilityRegistry`:

- `Withdraw` and `Enable` need an on-call operator and an incident id. `Enable` also needs the id of the evaluation run that cleared the candidate. Every attempt is audited, refused ones included.
- A capability nobody registered counts as withdrawn (fail closed).
- The propagators run inside the registry's lock, so the effect sees changes in the registry's order; run after it, a concurrent withdraw and enable can leave the effect open while the registry says withdrawn.
- The propagator revokes under its own source (`ops-switch`) on the security chapter's `RevocationList`, so an enable never lifts a security revocation it did not set.
- Callers and the model get a fixed refusal (`'reimburse' is paused`); the operator's reason stays in the audit.
- Lab limits: the on-call flag is passed by the caller (in production it comes from the identity provider's role claim), and the evaluation-run id is recorded, not looked up in the evaluation store.
- `Snapshot()` records the states before anything changes (preserve the facts first).
- Nothing caches the states at session start, so a withdrawal reaches sessions that are already open, and the same session works again after `Enable`.

Skills have their own switch: the skills chapter's `SkillRegistry.Withdraw` (`Ch16.SkillLifecycle`). A registry propagator can call it the same way it calls the revocation list here.

## Self-test

```bash
dotnet run --project src/Ch24.Operations/Ch24.KillSwitch -- --self-test
```

A scripted model (`AgenticAI.Testing.ScriptedToolChatClient`) does what the user asks and ignores its instructions. That is the failure the test needs. It proves wiring, never model behaviour. The test covers:

- the same session before, during and after the withdrawal (FAQ still answered, history intact);
- a developer who is not on call is refused, and two on-call operators withdrawing at once (`Task.Run` + `Barrier`) produce one change;
- 20 concurrent runs after the withdrawal are all refused;
- a call that was already past the tool-side check is denied at the effect;
- an unregistered tool is refused;
- 5000 withdraw-vs-enable races (`Task.Run` + `Barrier`) leave the registry and the effect in the same state;
- a security revocation outlives an ops withdraw and enable;
- re-enabling with no evaluation run is refused.

Two failures are reproduced (`DEFECT REPRODUCED`):

- an instruction ("be careful: do NOT call the reimburse tool") as the switch: the tool runs;
- hiding the tool from the model (`HideWithdrawnToolsChatClient`, below the function-invoking layer `ChatClientAgent` adds): the model asks for it by name and the function runs. Hiding plus the guard is refused.

A call that is already inside `ReimbursementService` when the switch moves is not stopped: withdrawal stops new invocations, it does not cancel or undo an effect.

Packages: `Microsoft.Agents.AI` (GA). No model, no network beyond 127.0.0.1, no API key.
