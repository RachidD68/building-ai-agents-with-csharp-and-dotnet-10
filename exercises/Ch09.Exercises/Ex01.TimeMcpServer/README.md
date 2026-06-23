# Exercise 01 — Time MCP Server (Beginner)

## Prompt

Build an MCP server exposing `GetCurrentTime(timezone)` and `ConvertTime(time, fromTz, toTz)` tools. Include a consumer that discovers and uses these tools through an agent.

## Structure

- `Server/` — The MCP server (stdio transport)
- `Consumer/` — The MCP consumer + agent

## Hints

- Use `[McpServerTool]` and `[McpServerToolType]` attributes.
- Use `WithToolsFromAssembly()` for automatic discovery.
- The consumer launches the server as a child process via `StdioClientTransport`.
- Use `TimeZoneInfo.FindSystemTimeZoneById()` for timezone operations.

## Running

```bash
# First build the server
dotnet build Server/

# Then run the consumer (it launches the server automatically)
dotnet run --project Consumer/
```

## Key Concepts

- MCP server with `[McpServerTool]` attributes
- Stdio transport for inter-process communication
- Tool discovery via `ListToolsAsync()`
- MCP tools integrated with `ChatClientAgent`
