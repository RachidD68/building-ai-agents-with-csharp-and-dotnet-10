# Approval Workflow: Contoso claim review

One expense claim goes through a typed review graph (`ClaimReview.cs`) and waits for a person:

```
classify --switch--> policy-check + receipt-check --barrier--> collect --> write (agent) --> claim-review port
         '--default--> returned                                    ^                 |
                                                     revise (max 2) <-- Revise        |
                                                              Approve --> approved    |
                                                              Reject  --> rejected  <-'
```

The `RequestPort` takes a `ReviewReply` (`Approve`, `Revise` or `Reject`), not a string. Only an explicit `approve` produces the `approved` outcome. An empty line, a typo, three wrong entries or the end of input send no reply at all: the run stays at `PendingRequests` and nothing is approved. A claim that is sent back more than twice ends as `not-approved`, because a loop that stops on its cap has not succeeded.

This chapter covers only the pause and the typed, fail-closed decision. What an approval is allowed to authorize (exact arguments, reviewer identity, expiry) belongs to the authorized-actions chapter.

## Key Concepts

| Concept | Description |
|---------|-------------|
| `AddSwitch` / `AddCase` / `WithDefault` | Route by claim category, then by the reviewer's decision |
| Switch case with two targets + `AddFanInBarrierEdge` | Run both checks in parallel and wait for both |
| `RequestPort.Create<ReviewRequest, ReviewReply>` | A typed pause; a reply of any other type is refused |
| `IWorkflowContext.QueueStateUpdateAsync` / `ReadStateAsync` | The last review request, shared by the writer and the revise step |
| `IResettableExecutor` | The barrier collector clears its per-run list if the graph is reused |
| `RunStatus.PendingRequests` | What "no decision" looks like from the host |

Switch predicates are written `r => r is { Decision: ReviewDecision.Approve }`: the switch can call a predicate with `null`, so a predicate that dereferences its argument throws inside the run.

## Running

```bash
dotnet run --project src/Ch14.Workflows/Ch14.ApprovalWorkflow                 # model + you
dotnet run --project src/Ch14.Workflows/Ch14.ApprovalWorkflow -- --offline    # scripted model
dotnet run --project src/Ch14.Workflows/Ch14.ApprovalWorkflow -- --self-test  # offline, RESULT n/n
```

Exit code `0` only when the claim is approved; `2` for pending, rejected, returned or not-approved. The live mode defaults to Ollama (`llama3.2`); configure others in `appsettings.json`. Every branch of the graph is tested without a model in `Ch14.WorkflowTests`, which compiles `ClaimReview.cs` directly.
