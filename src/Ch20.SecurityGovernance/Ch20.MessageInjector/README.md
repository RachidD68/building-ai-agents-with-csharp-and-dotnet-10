# Ch20.MessageInjector: safety context, per run and mid-run

- **`SafetyContextProvider : AIContextProvider`** runs once at the start of each
  agent run with that run's request messages, and adds instructions for that
  request only. It keeps no "current query" field, so concurrent runs on one
  provider instance cannot see each other's request.
- **`MessageInjectingChatClient`** (`ChatClientAgentOptions.EnableMessageInjection`)
  sits between `FunctionInvokingChatClient` and the model. The document tool
  enqueues a safety note when the text it retrieved looks like instructions;
  the note goes out with the next model call of the same run.

The self-test drives the real agent loop over a scripted model and checks what
each model call was sent: the provider runs once per run (not once per model
call, as the first edition said), the note reaches call 2 and not call 1, and
16 concurrent runs (`Task.Run`) each get their own instructions. It also
reproduces the first edition's shared `CurrentQuery` race.

## Running

```bash
dotnet run -- --self-test   # offline: RESULT n/n
dotnet run                  # live, against the configured provider
```
