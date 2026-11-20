# Sequential Pipeline

Two ways to run agents in sequence, side by side, and a three-stage content pipeline.

| Mode | What runs |
|------|-----------|
| `dotnet run` | Translator, then Summarizer, twice: a manual chain (the summarizer sees only the translation) and `AgentWorkflowBuilder.BuildSequential` (the summarizer sees the whole conversation: the memo and the translation) |
| `dotnet run -- --content` | Researcher, Writer, Editor with `BuildSequential`. The researcher calls a local lookup over the Contoso travel policy; the editor checks the draft's numbers against the research, which it sees because the conversation is passed along |

This project absorbs the first edition's ContentPipeline. Its simulated web search is replaced by the local policy lookup, so the pipeline prints no invented statistics.

The offline proof (same final text from both chains; what the second agent is shown; a string input reaches the first agent) is in `Ch13.MultiAgent.Tests`.

## Running

```bash
dotnet run --project src/Ch13.MultiAgentTopology/Ch13.SequentialPipeline
dotnet run --project src/Ch13.MultiAgentTopology/Ch13.SequentialPipeline -- --content
```
