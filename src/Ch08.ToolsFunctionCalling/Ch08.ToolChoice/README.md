# Chapter 8 -- Controlling Tool Choice (ChatOptions.ToolMode)

Supports the chapter's "Controlling tool choice" section. One weather agent is run three times against the same prompt, changing only `ChatOptions.ToolMode`, and the program reports whether the tool actually fired.

## Key Concepts

| Concept | Description |
|---------|-------------|
| `ChatToolMode.Auto` | The default; the model decides whether to call a tool (a null `ToolMode` is treated as Auto) |
| `ChatToolMode.None` | The model sees the tools but must not call them |
| `ChatToolMode.RequireAny` | The model must call at least one tool but picks which |
| `ChatToolMode.RequireSpecific("name")` | Forces one named function; returns a `RequiredChatToolMode` |
| `ChatClientAgentRunOptions` | Carries the per-run `ChatOptions` (including `ToolMode`) into `RunAsync` |

## Note on enforcement

Tool-choice enforcement is the **provider's** job, not MAF's. Frontier hosted models honor `None` and forced choice reliably; small local models served through Ollama (e.g. `llama3.2`) frequently ignore `None` and call the tool anyway. If a design depends on the model not calling a tool, omit that tool from the turn's `Tools` list rather than relying on `None`.

## Running

```bash
dotnet run --project src/Ch08.ToolsFunctionCalling/Ch08.ToolChoice
```
