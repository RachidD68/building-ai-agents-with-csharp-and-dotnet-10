# Chapter 8 -- Tool Approval (human-in-the-loop)

The flagship sample for the chapter's "Tool approval" section. A sensitive `refund_order` tool is wrapped in `ApprovalRequiredAIFunction`, so the run pauses for a decision instead of firing the tool. Demonstrates an approved path, a rejected path, and the new MAF 1.10 `ToolApprovalAgent` auto-approval rule for a read-only tool.

## Key Concepts

| Concept | Description |
|---------|-------------|
| `ApprovalRequiredAIFunction` | Wraps an `AIFunction` so the run pauses with a `ToolApprovalRequestContent` instead of executing |
| `ToolApprovalRequestContent` / `ToolApprovalResponseContent` | The pause request (`.ToolCall`) and the approve/reject reply (`request.CreateResponse(approved, reason)`) |
| Pause -> inspect -> decide -> resume | Inspect `response.Messages` for approval requests, build decisions, resume in the **same** `AgentSession` |
| `ChatOptions.AllowMultipleToolCalls = false` | Keeps each approval decision isolated (approval is all-or-nothing within one response) |
| `ToolApprovalAgent` + `AutoApprovalRules` | MAF 1.10 delegating agent that auto-approves trusted calls (experimental, diagnostic `MAAI001`) |

## Running

```bash
dotnet run --project src/Ch08.ToolsFunctionCalling/Ch08.ToolApproval
```

Uses the provider in `appsettings.json` (default: Ollama `llama3.2`, which supports tool calls).
