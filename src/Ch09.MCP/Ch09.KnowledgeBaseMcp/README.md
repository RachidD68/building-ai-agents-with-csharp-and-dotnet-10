# Chapter 9 -- The Model Context Protocol (Knowledge Base MCP Server)

An MCP server that reads Markdown files from a `docs/` folder and exposes a `SearchKnowledgeBase` tool. This bridges to Chapter 10 (RAG) by showing simple keyword-based retrieval from documents.

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
dotnet run --project src/Ch09.MCP/Ch09.KnowledgeBaseMcp
```
