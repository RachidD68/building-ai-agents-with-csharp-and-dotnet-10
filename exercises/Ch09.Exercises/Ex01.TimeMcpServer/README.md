# Exercise 01 — Time MCP Server (Basic)

## Prompt

Build a tiny MCP server in C# with **one** tool: `GetCurrentTime(string timezone)` that returns the current time in the specified IANA timezone. Run it as a child process of a MAF agent and verify the agent uses the tool to answer, "What time is it in Tokyo right now?".

## Structure

- `Ex01.TimeMcpServer/` (this folder) — The MCP server (stdio transport), exposing only `GetCurrentTime`
- `../Ex01.TimeMcpConsumer/` — The MCP consumer + agent

## Hints

- Copy the `Ch09.McpServer` project as a starting point; replace `ProductCatalogTools` with a single static method using `TimeZoneInfo` (`FindSystemTimeZoneById` + `ConvertTime`).
- Use `[McpServerTool]` and `[McpServerToolType]` attributes with `WithToolsFromAssembly()` for automatic discovery.
- The consumer side is `Ch09.McpConsumer` with the new server project path — it launches the server as a child process via `StdioClientTransport`.

## Accept

- The agent answers "What time is it in Tokyo?" with the tool's value.
- An id that is not IANA (`Mars/Olympus_Mons`) is a tool error (`McpException`, so `isError: true`), never a guessed time.
- The tool name on the wire is `get_current_time`: with no `Name`, the SDK derives a snake_case name from the method.

## Running

```bash
# First build the server (the consumer starts it with --no-build)
dotnet build exercises/Ch09.Exercises/Ex01.TimeMcpServer

# Then run the consumer (it launches the server automatically)
dotnet run --project exercises/Ch09.Exercises/Ex01.TimeMcpConsumer

# Offline check: the real server, a scripted model (RESULT n/n)
dotnet run --project exercises/Ch09.Exercises/Ex01.TimeMcpConsumer -- --self-test
```

## Key Concepts

- Single-tool MCP server with `[McpServerTool]`
- Stdio transport for inter-process communication
- Tool discovery via `ListToolsAsync()`
- MCP tools integrated with `ChatClientAgent`
