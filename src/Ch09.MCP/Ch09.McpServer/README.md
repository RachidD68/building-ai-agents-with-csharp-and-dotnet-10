# Chapter 9 -- The Model Context Protocol (MCP Server)

A console app MCP server that exposes product catalog tools over stdio transport. Clients (like Ch09.McpConsumer) connect to this server to discover and invoke tools.

## Exposed Tools

| Tool | Description |
|------|-------------|
| `ProductLookup` | Looks up a product by ID, returns name, SKU, price, stock |
| `InventoryCheck` | Checks inventory level by SKU, returns availability status |

## Key Concepts

| Concept | Description |
|---------|-------------|
| `[McpServerTool]` | Marks a method as an MCP-exposed tool |
| `[McpServerToolType]` | Marks a class that contains MCP tools |
| `WithToolsFromAssembly()` | Automatically discovers all `[McpServerToolType]` classes |
| `WithStdioServerTransport()` | Runs over standard I/O for child-process hosting |

## Running

```bash
dotnet run --project src/Ch09.MCP/Ch09.McpServer
```
