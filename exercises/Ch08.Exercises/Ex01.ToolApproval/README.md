# Exercise 01: tool-call approval, policy first (Intermediate)

## Prompt

Give an agent a read-only `get_order_status` and a gated `cancel_order(orderId, refundAmount, currency)`, both wrapped in `ApprovalRequiredAIFunction`. Auto-approve the status lookup with a `ToolApprovalAgent` rule. Before a reviewer sees a cancel request, run `Ch08.ActionPolicy` on it: reject a `Deny` without asking, send a `RequireApproval` to the reviewer and store the decision bound to the exact arguments, approve an `Allow`.

## Acceptance

- a rejected cancel never fires (side-effect counter = 0);
- the status lookup runs unattended;
- an approval reused with changed arguments is refused, without asking the reviewer.

## Key concepts

- `ApprovalRequiredAIFunction`, `ToolApprovalAgent`, `ToolApprovalAgentOptions.AutoApprovalRules`
- `ToolApprovalRequestContent.CreateResponse(approved, reason)`, resumed on the same `AgentSession`
- `ReimbursementPolicy.Evaluate` and `ApprovalRecord` from `Ch08.ActionPolicy` (the order's paid total stands in for the account balance)

## Run

```bash
dotnet run --project exercises/Ch08.Exercises/Ex01.ToolApproval                  # Ollama llama3.2 by default
dotnet run --project exercises/Ch08.Exercises/Ex01.ToolApproval -- --self-test   # scripted model, RESULT n/n
```
