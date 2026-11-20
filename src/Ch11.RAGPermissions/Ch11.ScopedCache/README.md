# Ch11.ScopedCache

Two caches, retrieved chunks and generated answers, that obey the same boundary as a fresh answer.

- **Key.** An HMAC-SHA256 of the authorization scope (tenant and access level, never the subject), the normalized question, the corpus or index version, the model and the prompt version. No question text and no identifier appear in a key.
- **Dependencies.** Each entry records the document versions it was built from. `Revoke(tenant, docId)` removes every entry built from that document, in both caches, at once. The TTL only bounds age; it is not revocation.
- **Delivery-time check.** `TryGet` serves an entry only if every document it depends on is still readable by the entry's scope under the current policy.
- **Revocation during a computation.** `BeginCompute` returns a token read before retrieval; a write whose tenant saw a revocation since is refused.

```bash
dotnet run --project src/Ch11.RAGPermissions/Ch11.ScopedCache -- --self-test   # RESULT n/n
```

No model: answers are built from the evidence ids over the chapter's authorized search. The HMAC secret in the sample is a constant; a real deployment keeps it in its secret store.
