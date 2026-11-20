# Contoso v3: FAQ with evidence

The Contoso expense assistant at the end of the RAG baseline chapter. Before filing a claim, an employee asks a policy question; the desk answers from retrieved handbook evidence, or it does not answer.

```
authenticated caller -> tenant check -> FaqSearchService (top 3, score threshold)
   -> nothing relevant: NoEvidence, abstain, the model is not called
   -> model with the excerpts ([Source: x] tags) -> citations parsed from the answer
   -> Answered (cites only retrieved sources) | Uncited | UnsupportedCitation | Declined
   -> evidence record: question id, caller, chunk ids and scores, citations, outcome
```

`FaqDesk` composes the chapter's `Ch10.FaqAgent` as it is (chunker, dimension check, `FaqSearchService`, evidence format and the handbook in `docs/`) and takes the caller from v2's `PermissionDirectory`, the same identity source the reimbursement desk uses. Each question is one call with its own excerpts and no shared session, so concurrent questions cannot see each other's evidence. An answer that cites nothing, or cites a source that was not retrieved for this question, is withheld; the employee sees a fixed message and the record keeps what the model said it cited.

The scenario: Alice (`contoso/E-1001`) asks whether ride-sharing is reimbursable on business travel, before she files the taxi claim v2 pays; Bob asks about cryptocurrency investments, which the handbook does not cover; a `fabrikam` caller is refused before any search. The self-test adds three models that do not back their answer (no citation, a citation of a source it was not given, a refusal) and six questions on one desk at the same moment.

## What this snapshot does not do

- The handbook desk (`FaqDesk`) still lets every Contoso employee read every handbook passage; the permission-aware path is `PolicyFaq`, below, over its own two-organization policy corpus. The evidence log of either desk lives in memory.
- A citation check proves the answer names a retrieved source, not that the sentence is supported by it.
- The 0.55 threshold is the FAQ agent's starting point for this corpus with `nomic-embed-text`; `Ch10.RetrievalEval` is where it is measured.

## Permissions, freshness and evidence (`PolicyFaq`)

The RAG-with-permissions chapter completes this milestone with `PolicyFaq`, compiled from that chapter's projects as linked files (`Ch11.AuthorizedSearch`, `Ch11.VersionedIngestion`, `Ch11.ScopedCache`) over its synthetic corpus (`src/Ch11.RAGPermissions/corpus`: `contoso` and `fabrikam`, three access levels, superseded and withdrawn versions, a management draft, a hostile annex).

```
question -> scoped cache (HMAC key over tenant + level and versions; delivery-time check)
         -> authorized search (tenant, level, active state in the store query; per-request agent)
         -> model -> evidence check (every cited id was supplied to this request)
         -> answer (+ a warning when a dated question falls under an older version) | named abstention
publish / withdraw -> versioned ingestion (SQLite, atomic activation, tombstones) -> revoke cache entries
```

The scenario: on Friday Alice's late-taxi answer cites taxi v1 and Bob gets it from the cache; on Monday taxi v2 is published, which revokes Friday's entry; Frank (Fabrikam) and Maria (a Contoso manager) ask the same question, and the management draft is indexed but never supplied; a question about a ride on 10 November gets a warning that version 1 applied then; the supplier annex's text makes the scripted model cite an id it was never given, so the answer is withheld and not cached, and the same note pasted into a claim is denied by v2's desk (`resource_scope`); the hotel policy is withdrawn while answers about it are cached; thirty concurrent questions from both organizations stay apart. `--self-test` runs the handbook desk's checks and then these.

What `PolicyFaq` does not do: the vector index is in memory and is rebuilt from the publish calls at start; one process owns the cache; the evidence check proves where a cited passage came from, not that it supports the sentence.

## Running

```bash
dotnet run --project contoso/v3-faq-evidence                  # Ollama: nomic-embed-text + llama3.2
dotnet run --project contoso/v3-faq-evidence -- --self-test   # keyword embeddings + scripted model; RESULT n/n
```

Against a real model the checks print "as expected" or "differs" instead of PASS and FAIL, because a real answer varies.

## Earlier and later snapshots

v3 references v2 (`Contoso.AuthorizedReimbursement`) for the identity directory and keeps its reimbursement desk unchanged. The receipts snapshot (v4) builds on v2's intake and does not use this desk.
