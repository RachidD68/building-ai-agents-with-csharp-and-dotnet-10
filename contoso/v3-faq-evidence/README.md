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

- Every Contoso employee can read every handbook passage. Which employee may see which document, freshness of the index and a durable evidence store are the next chapter's work, which completes this milestone.
- A citation check proves the answer names a retrieved source, not that the sentence is supported by it.
- The evidence log lives in memory and ends with the process.
- The 0.55 threshold is the FAQ agent's starting point for this corpus with `nomic-embed-text`; `Ch10.RetrievalEval` is where it is measured.

## Running

```bash
dotnet run --project contoso/v3-faq-evidence                  # Ollama: nomic-embed-text + llama3.2
dotnet run --project contoso/v3-faq-evidence -- --self-test   # keyword embeddings + scripted model; RESULT n/n
```

Against a real model the checks print "as expected" or "differs" instead of PASS and FAIL, because a real answer varies.

## Earlier and later snapshots

v3 references v2 (`Contoso.AuthorizedReimbursement`) for the identity directory and keeps its reimbursement desk unchanged. The receipts snapshot (v4) builds on v2's intake and does not use this desk.
