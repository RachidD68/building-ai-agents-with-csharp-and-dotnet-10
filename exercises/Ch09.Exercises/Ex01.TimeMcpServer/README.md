# Exercise 01 — Time MCP Server (Basic)

## Prompt

Build a tiny MCP server in C# with **one** tool: `GetCurrentTime(string timezone)` that returns the current time in the specified IANA timezone. Run it as a child process of a MAF agent and verify the agent uses the tool to answer, "What time is it in Tokyo right now?".

## Structure

- `Server/` — The MCP server (stdio transport), exposing only `GetCurrentTime`
- `Consumer/` — The MCP consumer + agent

## Hints

- Copy the `Ch09.McpServer` project as a starting point; replace `ProductCatalogTools` with a single static method using `TimeZoneInfo` (`FindSystemTimeZoneById` + `ConvertTime`).
- Use `[McpServerTool]` and `[McpServerToolType]` attributes with `WithToolsFromAssembly()` for automatic discovery.
- The consumer side is `Ch09.McpConsumer` with the new server project path — it launches the server as a child process via `StdioClientTransport`.

## Running

```bash
# First build the server
dotnet build Server/

# Then run the consumer (it launches the server automatically)
dotnet run --project Consumer/
```

## Key Concepts

- Single-tool MCP server with `[McpServerTool]`
- Stdio transport for inter-process communication
- Tool discovery via `ListToolsAsync()`
- MCP tools integrated with `ChatClientAgent`
