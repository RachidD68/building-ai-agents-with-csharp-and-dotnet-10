# Exercise 02 — Dictionary MCP (Intermediate)

## Prompt

Build an MCP server that exposes two tools: `DefineWord(string word)` and `GetSynonyms(string word)`, backed by a small in-memory dictionary (ten to twenty entries is plenty). Connect a MAF agent to it and run a conversation like "Define 'reticent' and give me three synonyms" — the agent should chain the two tools.

## Structure

- `Ex02.DictionaryMcpServer/` (this folder) — MCP server with the two dictionary tools and the in-memory dictionary
- `../Ex02.DictionaryMcpConsumer/` — MCP consumer + agent

## Hints

- The `ProductCatalogTools.cs` file in `Ch09.McpServer` has both the `ProductLookup` and `InventoryCheck` pattern you need: two static methods, one dictionary.
- The agent chains the two tools the same way the tools chapter's agent chained `multiply` and `convert_units`.

## Over HTTP (intermediate) and protected (challenge)

```bash
dotnet run --project exercises/Ch09.Exercises/Ex02.DictionaryMcpServer -- --http        # http://localhost:5001/mcp, no auth
dotnet run --project exercises/Ch09.Exercises/Ex02.DictionaryMcpConsumer -- --http
dotnet run --project exercises/Ch09.Exercises/Ex02.DictionaryMcpServer -- --protected   # needs Dictionary:Issuer
dotnet run --project exercises/Ch09.Exercises/Ex02.DictionaryMcpConsumer -- --self-test # offline, RESULT n/n
```

- A client pinned to `HttpTransportMode.Sse` cannot connect to the stateless server (the self-test records the 405).
- Challenge: `add_word` needs `dictionary.write`, the lookups `dictionary.read` (`ProtectedDictionaryTools.cs`, `DictionaryHttp.cs`). A `dictionary.read` token does not see `add_word` in `tools/list`, and calling it by name gets a JSON-RPC "Access forbidden" error (not an HTTP 403). The self-test signs its tokens with a local key.

## Key Concepts

- Two-tool MCP server backed by in-memory data
- `[McpServerTool]` / `[McpServerToolType]` attributes with `WithToolsFromAssembly()`
- Tool discovery and integration via `StdioClientTransport`
- Tool chaining across one MCP server
