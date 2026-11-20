# Exercise 02 — Multi-Agent API (Intermediate)

## Prompt

Host four keyed agents (translator, summarizer, formalizer, classifier) in one
API with `builder.AddAIAgent`, list them at `/agents`, and give each a
single-turn route. Add one conversational route whose conversation id belongs
to the caller who created it: the token's tenant (`tid`) and subject (`sub`)
are part of the key.

## Accepted when

- an unknown agent key returns 404;
- user B gets 404 on user A's `conversationId` (404, not 403: "not yours"
  looks the same as "no such id");
- A's second turn sees A's first turn.

## Routes

| Method | Path                                   | Kind                         |
|--------|----------------------------------------|------------------------------|
| GET    | `/agents`                              | discovery (anonymous)        |
| POST   | `/agents/{name}`                       | single-turn (bearer)         |
| POST   | `/agents/{name}/conversations`         | issue an owned id (bearer)   |
| POST   | `/agents/{name}/conversations/{id}`    | conversational (bearer)      |

## Run

```bash
dotnet run --project exercises/Ch17.Exercises/Ex02.MultiAgentApi -- --self-test
```

The conversations live in memory, in one process. The chapter's host keeps
them in the conversations chapter's SQLite store with a concurrency token.
