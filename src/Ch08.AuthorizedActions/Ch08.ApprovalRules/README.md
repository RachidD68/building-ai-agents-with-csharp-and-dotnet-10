# Ch08.ApprovalRules — Gating Excessive Agency (OWASP LLM06)

Human-in-the-loop on the **tool itself**, the MAF-native way. Backs the authorized-actions chapter's
"Excessive Agency" section.

## What It Shows

- **`ApprovalRequiredAIFunction`** (Microsoft.Extensions.AI) — wraps a dangerous
  `AIFunction` so the inner agent will not execute it; it surfaces a
  `ToolApprovalRequestContent` instead.
- **`ToolApprovalAgent`** (Microsoft.Agents.AI) — middleware that queues approval
  requests and applies `AutoApprovalRules`.
- **Argument validation as an auto-approval rule** — small refunds (`<= $50`) are
  auto-approved; larger ones escalate to a human. Least-privilege + validation in
  one hook.
- **The approve / reject / resume loop** — inspect each request, reply with
  `request.CreateResponse(approved, reason)`, resume on the **same** session.
  Approve → the tool runs. Reject → it never executes.

| Scenario | Tool call | Decision | Result |
|----------|-----------|----------|--------|
| Delete C-1001 | `delete_customer_record` | **approve** | record deleted |
| Refund $5000 | `issue_refund` | **reject** (over policy) | not executed |
| Refund $20 | `issue_refund` | rule **auto-approves** | refunded, no human |

## API note

`ToolApprovalAgent` / `ToolApprovalAgentOptions` were `[Experimental("MAAI001")]`
through MAF 1.13 and are stable in 1.28, so the `.csproj` has no `<NoWarn>`
for them. The `ApprovalRequiredAIFunction` / `ToolApprovalRequestContent` /
`ToolApprovalResponseContent` types in Microsoft.Extensions.AI are stable.

The tools chapter covers the per-function mechanics; this project is the **safety**
angle. Contrast with the workflows chapter's workflow-level `RequestPort` human-in-the-loop
(different granularity; both belong in a real system).

## Running

```bash
# Defaults to Ollama + llama3.2 (see appsettings.json).
dotnet run
```
