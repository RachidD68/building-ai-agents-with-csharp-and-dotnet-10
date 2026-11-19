# Ch08.ToolApproval

The framework's approval mechanics, with the application's policy checked first. This project merges the first edition's tool-approval sample and its approval-rules sample (`ToolApprovalAgent` rules, the invariant-culture amount rule) into one.

An order-administration agent has a gated `refund_order(orderId, amount, currency)` and a read-only `get_order_status`. Both are wrapped in `ApprovalRequiredAIFunction`; a `ToolApprovalAgent` rule auto-approves the status lookup, and a refund only when the amount rule and ActionPolicy both say yes.

For every approval request that surfaces, `ApprovalLoop.RunAsync` evaluates `Ch08.ActionPolicy` **before** anyone is asked:

| Policy | What happens |
|---|---|
| `Deny` (negative amount, wrong currency, unknown order, an approval reused with other arguments) | rejected with the reason code; no reviewer sees it |
| `RequireApproval` | the reviewer decides; the decision is stored server-side, bound to the exact arguments, and the policy runs again (the requester cannot approve their own refund) |
| `Allow` | approved |

The tool body checks the policy once more and goes through a per-order ledger: one credit per order, however many times an approved call runs. Approval authorizes; the ledger stops repetition. Framework approval request ids pair a response with its request; the business operation id keys the effect.

| Concept | Description |
|---|---|
| `ApprovalRequiredAIFunction` | Wraps an `AIFunction` so the run pauses with a `ToolApprovalRequestContent` instead of executing |
| `ToolApprovalRequestContent` / `ToolApprovalResponseContent` | The pause request (`.ToolCall`) and the reply (`request.CreateResponse(approved, reason)`), sent back on the **same** `AgentSession` |
| `ToolApprovalAgent` + `AutoApprovalRules` | Rules see `ToolAutoApprovalRuleContext.FunctionCallContent` and return true to skip the pause |
| `ChatOptions.AllowMultipleToolCalls = false` | One call, and one decision, per turn (approval is all-or-nothing within one response) |

No `NoWarn`: in Microsoft.Agents.AI 1.28.0 and Microsoft.Extensions.AI.Abstractions 10.11.0 these types carry no `[Experimental]` attribute.

## Running

```bash
dotnet run --project src/Ch08.AuthorizedActions/Ch08.ToolApproval                  # Ollama llama3.2 by default
dotnet run --project src/Ch08.AuthorizedActions/Ch08.ToolApproval -- --self-test   # offline
```

The self-test runs three suites under one `RESULT` line: the per-order ledger, the amount rule under `de-DE` ("1,5" is not 1.5), and the real `ToolApprovalAgent` loop driven by a scripted model.
