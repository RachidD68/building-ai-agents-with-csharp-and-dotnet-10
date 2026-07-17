// ============================================================================
// Chapter 9 — HTTP-hosted MCP server (streamable HTTP transport)
//
// The stdio server (Ch09.McpServer) and this one expose the SAME tools
// (ProductCatalogTools, linked in via the .csproj) — only the transport
// changes. AddMcpServer().WithHttpTransport() + app.MapMcp() serve the tools
// over a single /mcp endpoint. Point a client at http://localhost:5000/mcp
// with HttpTransportMode.StreamableHttp (NOT Sse — a client pinned to the
// legacy SSE mode gets an empty tool list from a streamable-HTTP server).
//
// This is the sample behind the Chapter 9 "swap WithStdioServerTransport for
// WithHttpTransport" Pro Tip and the streamable-HTTP Challenge exercise.
// ============================================================================

using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// Same registration as the stdio host — only the transport line changes.
builder.Services
    .AddMcpServer()
    .WithHttpTransport()        // streamable HTTP instead of WithStdioServerTransport()
    .WithToolsFromAssembly();   // discovers [McpServerToolType] classes in this assembly

var app = builder.Build();

app.MapMcp();   // routes the /mcp endpoint — without this line, nothing is listening

app.Run("http://localhost:5000");
