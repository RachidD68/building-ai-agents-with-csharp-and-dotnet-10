# The Model Context Protocol (Knowledge Base MCP Server)

An MCP server over stdio that reads the Markdown files in `docs/` and offers them two ways:

| Primitive | Name | Who uses it |
|-----------|------|-------------|
| Tool | `SearchKnowledgeBase(query)` | the model: keyword match, ranked by distinct terms matched, top 3 excerpts |
| Resource template | `kb://docs/{name}` | an application: reads one document by name (for example to index it) |

`KeywordMatcher.cs` holds the matching rules (terms of 3+ characters, case-insensitive, ranked, ties by name). It matches words, not meaning: "vacation" finds nothing although `leave-and-pto.md` is about it. The RAG chapter keeps the tool and replaces the matcher with embeddings.

Documents are looked up by name among the files that exist; a name is never joined into a path, so `../appsettings` finds nothing.

## Included Documents

| Document | Topic |
|----------|-------|
| `remote-work-policy.md` | Remote work eligibility, schedule, equipment, security |
| `expense-reimbursement.md` | Expense submission process, eligible expenses, limits |
| `onboarding-guide.md` | New employee first two weeks, setup, key contacts |
| `leave-and-pto.md` | PTO accrual, sick leave, parental leave, bereavement |
| `code-of-conduct.md` | Core values, expected behavior, reporting concerns |

## Running

```bash
dotnet run --project src/Ch09.MCP/Ch09.KnowledgeBaseMcp                    # serve over stdio
dotnet run --project src/Ch09.MCP/Ch09.KnowledgeBaseMcp -- --self-test     # offline, RESULT n/n
```
