# Exercise 02 — Dictionary MCP (Intermediate)

## Prompt

Build an MCP server that exposes two tools: `DefineWord(string word)` and `GetSynonyms(string word)`, backed by a small in-memory dictionary (ten to twenty entries is plenty). Connect a MAF agent to it and run a conversation like "Define 'reticent' and give me three synonyms" — the agent should chain the two tools.

## Structure

- `Server/` — MCP server with the two dictionary tools and the in-memory dictionary
- `Consumer/` — MCP consumer + agent

## Hints

- The `ProductCatalogTools.cs` file in `Ch09.McpServer` has both the `ProductLookup` and `InventoryCheck` pattern you need: two static methods, one dictionary.
- The agent chains the two tools the same way the Chapter 8 agent chained `multiply` and `convert_units`.

## Key Concepts

- Two-tool MCP server backed by in-memory data
- `[McpServerTool]` / `[McpServerToolType]` attributes with `WithToolsFromAssembly()`
- Tool discovery and integration via `StdioClientTransport`
- Tool chaining across one MCP server
