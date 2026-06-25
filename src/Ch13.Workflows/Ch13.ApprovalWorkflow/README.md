# Chapter 12 -- Approval Workflow (Capstone)

Demonstrates a human-in-the-loop approval workflow. An LLM agent drafts a document, a validator agent reviews it, then the workflow pauses for human approval via `RequestPort`. Rejected drafts loop back to the drafter with feedback for revision.

## Key Concepts

| Concept | Description |
|---------|-------------|
| `RequestPort` | Pauses workflow execution for external (human) input |
| `StreamingRun` | Enables interaction with a running workflow |
| `RequestInfoEvent` | Signals that the workflow needs human input |
| `ExternalRequest.CreateResponse()` | Sends human decisions back to the workflow |
| Revision loop | Rejected drafts cycle back for improvement |
| `WorkflowVisualizer` | Generates a Mermaid diagram of the approval flow |

## Running

```bash
dotnet run --project src/Ch12.Workflows/Ch12.ApprovalWorkflow
```
