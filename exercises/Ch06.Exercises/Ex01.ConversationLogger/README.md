# Exercise 01: Conversation Logger (Basic)

## Prompt

Log every turn of a persisted conversation with a timestamp, the session id and the role, to a file that outlives the process. After a restart the log still holds the earlier turns and new turns append to it; the log of one session never contains message text from another session.

## How it works

- It builds on `Ch06.PersistentSession`: each named session is serialized to `conversations/sessions/<id>.json` after every turn and restored with `DeserializeSessionAsync` on the next run.
- After each `RunAsync` it reads the messages the history provider holds for that session (`InMemoryChatHistoryProvider.GetMessages`) and appends the ones not logged yet to `conversations/logs/<id>.log`, one line per message.
- One log file per session, so isolation doesn't depend on filtering a shared file. The session id becomes a file name, so only letters, digits and dashes are accepted.

## Run it

```
dotnet run -- alice      # first run: a new session, one turn
dotnet run -- alice      # second run: restored, the log keeps growing
dotnet run -- --self-test
```

A log line looks like this (the text is the model's, so yours will differ):

```
<yyyy-MM-dd HH:mm:ss.fff> [alice] [user     ] I'm planning a trip to Lisbon and I like walking tours.
```

## Self-test

`--self-test` needs no model. A fake `IChatClient` (from `AgenticAI.Testing`) answers two turns for `alice` and one for `bob`; then a new client, agent and history provider restore `alice` from its file, as a restart would. It checks that the restored session reached the model, that the earlier log lines are unchanged and the new turn is appended, that every line carries its timestamp, session id and role, that neither log holds the other session's text, and that a session id like `../alice` is refused. It ends with `RESULT 5/5`.
