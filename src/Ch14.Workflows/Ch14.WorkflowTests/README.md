# Workflow Tests

Every branch of the chapter's graphs, run in process without a model. A plain console harness (no xUnit dependency): each case builds a graph, runs it, and asserts on the streamed events and the outcome. It prints `RESULT n/n` and exits `0` only when every check passes.

| Section | What it proves |
|---------|----------------|
| Sentiment router | `AddEdge<T>` predicates: one case per branch |
| Claim review | The `Ch14.ApprovalWorkflow` graph (its `ClaimReview.cs` is compiled in): approve, revise then approve, reject, switch default, empty line / end of input / unknown word left pending, revision cap reached = not approved, a string reply refused by the typed port (the same run stays pending), a decision that is none of the three = not approved, both checks in one superstep, null calls of the switch after the port (and the exception an unsafe predicate throws) |
| Where it fails | `DEFECT REPRODUCED` lines for the first edition's "anything but revise = approve" rule, a stateful executor shared across runs, an uncapped revise loop; plus the same reply sent twice (MAF reports no pending request for that id and the run ends with no outcome), a second run while the first owns the graph (refused), and two runs started at once behind a barrier (exactly one refused, 20 rounds) |
| Declarative (YAML) | `DeclarativeWorkflowBuilder.Build<string>` with an in-memory `ResponseAgentProvider`; build per run vs. one reused instance; restore from a checkpoint into a fresh build; `MaximumExpressionLength` (Build accepts the file, and in 1.28.0 the run still evaluates the over-limit expression: `DEFECT REPRODUCED`); the HTTP contract against two loopback listeners, enforced by `DefaultHttpRequestHandler` 1.28's own allow-list (host and port; a primary handler that follows redirects or keeps cookies is refused; each redirect hop is checked) |

The declarative cases run `Microsoft.Agents.AI.Workflows.Declarative` 1.28.0 on Workflows 1.28.0. Two results changed from 1.20.0: a reused built workflow no longer carries `Global` state into the next run, and `MaximumExpressionLength` is no longer enforced on these expressions. Re-run the harness after a package bump before you rely on either.

## Running

```bash
dotnet run --project src/Ch14.Workflows/Ch14.WorkflowTests
```

Fully offline: the scripted `IChatClient` stands in for the model and the HTTP cases use only `127.0.0.1`. To port a case to xUnit, move it into a `[Fact]` and replace `Harness.Check(...)` with `Assert`.
