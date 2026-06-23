# Ch15.ToolApproval — Gating Excessive Agency (OWASP LLM06)

Human-in-the-loop on the **tool itself**, the MAF-native way. Backs Chapter 15's
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

`ToolApprovalAgent` / `ToolApprovalAgentOptions` are `[Experimental("MAAI001")]`
in MAF 1.10 — suppressed with `<NoWarn>$(NoWarn);MAAI001</NoWarn>` in the
`.csproj`. The `ApprovalRequiredAIFunction` / `ToolApprovalRequestContent` /
`ToolApprovalResponseContent` types in Microsoft.Extensions.AI are stable.

Chapter 8 covers the per-function mechanics; this project is the **safety**
angle. Contrast with Chapter 12's workflow-level `RequestPort` human-in-the-loop
(different granularity; both belong in a real system).

## Running

```bash
# Defaults to Ollama + llama3.2 (see appsettings.json).
dotnet run
```
