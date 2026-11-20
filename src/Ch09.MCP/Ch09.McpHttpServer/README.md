# The Model Context Protocol (HTTP-hosted MCP Server)

An ASP.NET Core MCP server that exposes the **same** product catalog tools as `Ch09.McpServer`, over **streamable HTTP** instead of stdio. The `.csproj` links `ProductCatalogTools.cs` from `Ch09.McpServer`; `CatalogMcpEndpoint.cs` registers it with `WithTools(...)` and maps `/mcp`. Only the transport changes.

**No authentication.** Any caller that can reach the port lists and calls every tool. `Ch09.McpAccessTests` reproduces that (`DEFECT REPRODUCED` lines), and `Ch09.ProtectedMcpServer` is the same endpoint as an OAuth resource server.

## Exposed Tools

| Tool | Description |
|------|-------------|
| `ProductLookup` | Looks up a product by ID, returns name, SKU, price, stock |
| `InventoryCheck` | Checks inventory level by SKU, returns availability status |

## Key Concepts

| Concept | Description |
|---------|-------------|
| `Microsoft.NET.Sdk.Web` | Required for `WebApplication.CreateBuilder` |
| `WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)` | Streamable HTTP; stateless, no `Mcp-Session-Id` (the SDK default since the 2026-07-28 revision, set explicitly) |
| `MapMcp("/mcp")` | Routes the endpoint; the no-argument `MapMcp()` serves at `/` |

## Running

```bash
dotnet run --project src/Ch09.MCP/Ch09.McpHttpServer
dotnet run --project src/Ch09.MCP/Ch09.McpConsumer -- --http http://localhost:5000/mcp
```

The server listens on `http://localhost:5000` and serves MCP at `http://localhost:5000/mcp`.

## Connecting a client

```csharp
HttpClientTransport transport = new(new HttpClientTransportOptions
{
    Endpoint = new Uri("http://localhost:5000/mcp"),
    TransportMode = HttpTransportMode.StreamableHttp,   // NOT Sse
});
```

> **Watch the transport mode.** In stateless mode the server has no `/sse` endpoint. A client pinned to `HttpTransportMode.Sse` fails to connect: the `Ex02.DictionaryMcpConsumer` self-test records `HttpRequestException ... 405 (Method Not Allowed)` (MCP C# SDK, version pinned in `Directory.Packages.props`). Use `StreamableHttp`.
