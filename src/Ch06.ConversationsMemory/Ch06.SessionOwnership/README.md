# Ch06.SessionOwnership — A Conversation That Belongs to Someone

A persisted session is user data. This project stores agent sessions in SQLite through a custom `AgentSessionStore` and checks, offline, the three properties an owned conversation needs.

## What it shows

- The store key comes from the authenticated caller: `ConversationKeys.For(caller, conversationId)` builds an `AgentSessionStoreKey` with a `tenant` and a `subject` partition. A store key built from the chat payload (`PayloadKeyedConversations`) is the bug, and the program reproduces it before the fix.
- `SqliteSessionStore` keys every row by `(tenant, subject, session id)`, so another subject's key finds nothing and a delete reaches only the owner's row. `DeleteSessionAsync` treats a missing session as a no-op, so deleting twice succeeds twice.
- Each row has a schema version (a row from an unknown schema is refused) and a version number used as a concurrency token. The version a session was loaded at travels in its `StateBag` and is checked by the save (`UPDATE ... WHERE version = @loaded`). Two writers that loaded the same version get one success and one `SessionConflictException`, and a window opened before a delete cannot save the conversation back.
- A key without both partitions, or with an extra one, is refused.

## Experimental API

`AgentSessionStore`, `AgentSessionStoreKey` and `DelegatingAgentSessionStore` are `[Experimental("MAAI001")]` in Microsoft.Agents.AI 1.28.0. The project opts in with `<NoWarn>`.

## Run

```bash
dotnet run -- --self-test     # RESULT n/n; the default mode runs the same suite
```

No model: `EchoChatClient` answers. The database lives in a temporary folder that is deleted at the end.

## What this does not prove

The offline test runs on one machine against one SQLite file. It does not prove retention rules, deletion from backups or replicas, an authenticated HTTP route, or concurrency across database servers.
