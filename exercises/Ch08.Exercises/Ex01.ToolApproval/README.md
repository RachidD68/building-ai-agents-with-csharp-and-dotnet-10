# Exercise 04 — Tool-call Approval (Intermediate)

## Prompt

Give an agent two tools — a read-only `get_order_status` and a write-side
`cancel_order` — and wrap both in `ApprovalRequiredAIFunction`. Use an
`AutoApprovalRule` to auto-approve the read-only call but require a manual
decision for the write call. Verify a rejected `cancel_order` never executes.

## Key Concepts

- `ApprovalRequiredAIFunction` — gating a tool on approval (OWASP LLM06)
- `ToolApprovalAgent` / `ToolApprovalAgentOptions.AutoApprovalRules`
- `ToolApprovalRequestContent.CreateResponse(approved, reason)`
- Resuming the approval loop on the SAME `AgentSession`
- Proving a rejected destructive call never runs (side-effect counter)

## Run

`dotnet run` (defaults to Ollama / llama3.2). The side-effect counter must
report 0 cancellations after the rejected scenario.
