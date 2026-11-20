# Ch18.AguiClient: a C# client that shows state, not words

- `AguiRunView` applies AG-UI events by identity: run events only for the run
  id it sent, text by message id (a second `TEXT_MESSAGE_START` is ignored),
  tool status by tool call id.
- A gap (content for a message that never started) or a stream that ends
  without `RUN_FINISHED` or `RUN_ERROR` discards the partial text and resyncs
  the operation from `/conversations/{thread}/snapshot`. The host's AG-UI
  events carry no SSE id, so a cut run cannot resume from a cursor.
- `ContosoClient` prints DONE only for a terminal state of the operation,
  which it follows on the status stream (`StatusView`), never for
  `RUN_FINISHED`.
- The same host is also driven through MAF's `AGUIChatClient`
  (`Microsoft.Agents.AI.AGUI`) and `AsAIAgent().RunStreamingAsync`.

## Run

```bash
dotnet run --project src/Ch18.StreamingInterfaces/Ch18.AguiClient -- --self-test
dotnet run --project src/Ch18.StreamingInterfaces/Ch18.AguiClient
```

Both start `Ch18.AguiServer` in process (TestServer, minted tokens). The
self-test uses a fake model; `dotnet run` uses the `LlmProvider` model
(Ollama llama3.2 by default); when the model submits the claim, a reviewer
approves it and the client follows the status stream to its terminal state.

**Preview.** `Microsoft.Agents.AI.AGUI` 1.5.0-preview. Simulated effects only.
