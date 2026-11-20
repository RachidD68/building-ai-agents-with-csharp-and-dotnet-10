# Workflow Tests

Every branch of the chapter's graphs, run in process without a model. A plain console harness (no xUnit dependency): each case builds a graph, runs it, and asserts on the streamed events and the outcome. It prints `RESULT n/n` and exits `0` only when every check passes.

| Section | What it proves |
|---------|----------------|
| Sentiment router | `AddEdge<T>` predicates: one case per branch |
| Claim review | The `Ch14.ApprovalWorkflow` graph (its `ClaimReview.cs` is compiled in): approve, revise then approve, reject, switch default, empty line / end of input / unknown word left pending, revision cap reached = not approved, a string reply refused by the typed port |
| Where it fails | `DEFECT REPRODUCED` lines for the first edition's "anything but revise = approve" rule, a stateful executor shared across runs, an uncapped revise loop; plus the same reply sent twice (MAF reports no pending request for that id) and a concurrent second run of one graph (refused) |
| Declarative (YAML) | `DeclarativeWorkflowBuilder.Build<string>` with an in-memory `ResponseAgentProvider`; build per run vs. one reused instance; restore from a checkpoint into a fresh build; `MaximumExpressionLength`; the HTTP redirect contract against two loopback listeners (an allow-list `IHttpRequestHandler` with redirects off) |

The declarative cases run `Microsoft.Agents.AI.Workflows.Declarative` 1.20.0 on top of Workflows 1.28.0 (the newest Declarative package in the offline cache this repo restores from). The "reused instance carries `Global` state" line describes that pairing; re-run the harness after a package bump before you rely on it.

## Running

```bash
dotnet run --project src/Ch14.Workflows/Ch14.WorkflowTests
```

Fully offline: the scripted `IChatClient` stands in for the model and the HTTP cases use only `127.0.0.1`. To port a case to xUnit, move it into a `[Fact]` and replace `Harness.Check(...)` with `Assert`.
