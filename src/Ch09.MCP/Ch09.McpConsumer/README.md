# The Model Context Protocol (MCP Consumer)

Connects to an MCP server, discovers its tools, and feeds them to a `ChatClientAgent` (`CatalogAgent.cs`). The agent code is identical to the tools chapter; the only difference is where the tools come from.

## Modes

```bash
dotnet run --project src/Ch09.MCP/Ch09.McpConsumer                                   # stdio: launches Ch09.McpServer
dotnet run --project src/Ch09.MCP/Ch09.McpConsumer -- --http http://localhost:5000/mcp  # streamable HTTP (start Ch09.McpHttpServer first)
dotnet run --project src/Ch09.MCP/Ch09.McpConsumer -- --self-test                     # offline, RESULT n/n
```

Build the solution (or at least `Ch09.McpServer`, in the same configuration) first: the stdio mode and the self-test both start the built `Ch09.McpServer.dll` from `bin/<Configuration>/net10.0`.

The self-test runs the real stdio server as a child process and replaces the LLM with a scripted model. It checks discovery, the tools offered to the model, that a tool result reaches the model, that a tool the server never offered is not invoked, and that the server's logs arrive on stderr.

## Tokens: the destination guard

With `--http` and a pre-issued token in the `MCP_ACCESS_TOKEN` environment variable, the consumer builds its transport with `McpDestinationGuard.CreateCheckedTransportOptionsAsync`: the endpoint must be listed in `McpConsumer:AllowedServers` (appsettings.json), and the server's protected resource metadata must name that same endpoint, before the token is put on any request. The consumer does not request tokens from an authorization server itself. Both rules are tested offline in `Ch09.McpAccessTests`. Outside loopback, use HTTPS for the endpoint, the metadata and the issuer.

## Key Concepts

| Concept | Description |
|---------|-------------|
| `StdioClientTransport` | Launches the MCP server as a child process |
| `HttpClientTransport` + `HttpTransportMode.StreamableHttp` | Connects to a remote `/mcp` endpoint |
| `McpClient.CreateAsync()` | Connects to the server |
| `ListToolsAsync()` | Discovers available tools from the server |
| `McpClientTool` | An `AIFunction`; plugs directly into the agent's `tools` |
