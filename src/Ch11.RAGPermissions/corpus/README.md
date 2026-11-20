# Two-organization policy corpus

A synthetic corpus written for this book's RAG-with-permissions chapter. Every company, person, amount and rule in it is invented; none of it describes a real employer.

| Field (front matter) | Meaning |
|---|---|
| `doc`, `version` | document id and version; an evidence id is `<doc>@v<version>#<paragraph>` |
| `tenant` | `contoso` or `fabrikam`: the organization whose employees may read it |
| `access` | `staff` < `manager` < `restricted`: the lowest level that may read it |
| `state` | `active`, `superseded`, `draft` or `revoked` (the state the source system reports) |
| `effective` | the first day the version applies (applicability, not publication) |

What it contains on purpose: two versions of each organization's taxi policy (one superseded), a management draft that is not in force, a withdrawn (revoked) document per organization, one document per access level, and one hostile annex per organization whose text tries to give the assistant instructions and names a citation id it was never given.

Corpus version: see `VERSION`. License: see `LICENSE.md`.
