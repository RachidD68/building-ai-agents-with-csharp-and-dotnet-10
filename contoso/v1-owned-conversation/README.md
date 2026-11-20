# Contoso v1: owned conversation

The Contoso expense assistant at the end of the conversations chapter. An employee starts a claim, comes back after a restart to finish it, and can ask the assistant to forget it.

```
authenticated caller (tenant, subject) + conversation id
   -> AgentSessionStoreKey (tenant and subject partitions)
   -> SqliteSessionStore: load or create -> agent turn -> save (version check)
   -> forget: DeleteSessionAsync, owner only, a no-op when repeated
```

`ExpenseConversations` composes the chapter's `Ch06.SessionOwnership` project as it is: `SqliteSessionStore`, `ConversationKeys` and `OwnedConversations`. Identity comes from the caller the host authenticated (`EmployeeDirectory` stands in for the host's identity system); a request carries the conversation id and the text, and any tenant or subject written into it is ignored.

The scenario: Alice (`contoso/E-1001`) starts her taxi claim (64.80 EUR, the same claim the authorized-reimbursement snapshot pays); the process restarts and she asks which amount she gave, which is answered from the restored conversation; Bob (`contoso/E-2002`) sends her conversation id with her tenant and subject in the request and gets a new, empty conversation of his own; Alice has the claim open in two windows that send at the same moment, and one is answered while the other is told to reload; Alice asks to forget the conversation twice, and after another restart it is gone while Bob's survives.

## What this snapshot does not do

- No authenticated route. The callers are constructed as the host's authentication would produce them; the API and interface snapshot adds ASP.NET Core and real authentication.
- No retry on a conflict. The losing window is told to reload and send again; retrying the turn automatically is the chapter's challenge exercise (`Ex05.ConcurrentWrites`).
- Deletion covers the live row only. Backups, replicas, logs and traces that copied the conversation need their own retention rules.
- One SQLite file. The store's concurrency check holds for writers of one database.

## Running

```bash
dotnet run --project contoso/v1-owned-conversation                  # Ollama llama3.2 by default
dotnet run --project contoso/v1-owned-conversation -- --self-test   # scripted model, no network; RESULT n/n
```

The database lives in a temporary folder that is deleted at the end. Against a real model the checks are printed as "as expected" or "differs" instead of PASS and FAIL, because the wording of an answer varies.

## Earlier and later snapshots

This snapshot stands alone. It does not reference `v0-agent-fake-model`: it defines its own scripted model (`ScriptedExpenseModel`) and agent options here, and composes `Ch06.SessionOwnership` for the conversation.

`v2-authorized-reimbursement` keeps this snapshot's tenant and employees (contoso, E-1001, E-2002) and its rule that identity comes from authentication, never from the payload. It is self-contained: it does not reference this project and does not keep a conversation.
