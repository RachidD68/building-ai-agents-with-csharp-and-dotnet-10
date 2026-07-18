# Chapter 9 -- The Model Context Protocol (HTTP-hosted MCP Server)

An ASP.NET Core MCP server that exposes the **same** product catalog tools as `Ch09.McpServer`, but over the **streamable HTTP** transport instead of stdio. This is the sample behind the chapter's "swap `WithStdioServerTransport` for `WithHttpTransport`" Pro Tip and the streamable-HTTP Challenge exercise.

The tool code is not duplicated -- the `.csproj` links `ProductCatalogTools.cs` from `Ch09.McpServer`, so `WithToolsFromAssembly()` finds the `[McpServerToolType]` classes in this assembly. Only the transport changes.

## Exposed Tools

| Tool | Description |
|------|-------------|
| `ProductLookup` | Looks up a product by ID, returns name, SKU, price, stock |
| `InventoryCheck` | Checks inventory level by SKU, returns availability status |

## Key Concepts

| Concept | Description |
|---------|-------------|
| `Microsoft.NET.Sdk.Web` | Required for `WebApplication.CreateBuilder` |
| `WithHttpTransport()` | Serves MCP over streamable HTTP instead of stdio |
| `app.MapMcp()` | Routes the `/mcp` endpoint -- **without this line nothing is listening** |
| `<Compile Include="../Ch09.McpServer/ProductCatalogTools.cs" />` | Links the tools rather than copying them |

## Running

```bash
dotnet run --project src/Ch09.MCP/Ch09.McpHttpServer
```

The server listens on `http://localhost:5000` and serves MCP at `http://localhost:5000/mcp`.

## Connecting a client

Point a client at the `/mcp` endpoint using `HttpClientTransport`:

```csharp
HttpClientTransport transport = new(new HttpClientTransportOptions
{
    Endpoint = new Uri("http://localhost:5000/mcp"),
    TransportMode = HttpTransportMode.StreamableHttp,   // NOT Sse
});
```

> **Watch the transport mode.** A client pinned to `HttpTransportMode.Sse` talking to a streamable-HTTP server gets an **empty tool list** rather than an error -- the agent then behaves as if the server exposes nothing. `StreamableHttp` (or `AutoDetect`) is what you want. Standalone SSE is the deprecated transport, kept only for connecting to legacy servers.

## Related

| Project | Transport |
|---------|-----------|
| `Ch09.McpServer` | stdio (child process, same machine) |
| `Ch09.McpHttpServer` | streamable HTTP (remote) |
| `Ch09.McpConsumer` | MAF agent that consumes the stdio server |
