# Ch11.VersionedIngestion

Ingestion as a versioned process, in one SQLite file. Each source version moves through `Discovered -> Validated -> Indexed -> Active -> Superseded`, or to `Deleted`; only an `Active` version is searchable.

- **Idempotent.** A version row is unique by tenant, document and content hash: ingesting the same bytes again finds the row it already has. The same version number with other bytes is refused (`version_conflict`). Eight concurrent ingestions of one source leave one row and one activation.
- **Atomic activation.** Superseding the old version and activating the new one is one `BEGIN IMMEDIATE` transaction, and a partial unique index allows one active version per document. A late retry of an older version is refused (`older_than_active`), and so is a version whose effective date has not come (`not_yet_effective`; it stays `Indexed`).
- **Effective dates.** `ApplicableOn` answers which version applied on a day; a version that is no longer current comes with a warning.
- **Deletion.** A tombstone, the chunks removed from every index version, and a change notice for the caches. The same bytes ingested again stay deleted, and an indexing run that a delete overtakes writes no chunks (the state and the tombstone are re-read inside its write transaction).
- **Index versions.** A new embedding model is a new index version, built beside the old one and switched to deliberately; a query labelled with another model, or of another length, is refused (`IndexMismatchException`). The label is the caller's word: two models with the same dimensions are told apart only by it.

```bash
dotnet run --project src/Ch11.RAGPermissions/Ch11.VersionedIngestion -- --self-test   # RESULT n/n
```

No model and no network: `HashingEmbedder` with two seeds stands in for two embedding models.
