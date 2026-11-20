// ============================================================================
// HTTP-hosted MCP server (streamable HTTP transport)
//
// The stdio server (Ch09.McpServer) and this one expose the SAME tools
// (ProductCatalogTools, linked in via the .csproj); only the transport
// changes. WithHttpTransport() + MapMcp("/mcp") serve the tools over a single
// endpoint. Point a client at http://localhost:5000/mcp with
// HttpTransportMode.StreamableHttp.
//
// Nothing here checks who the caller is: any process that can reach the port
// can list and call every tool. Ch09.McpAccessTests reproduces that, and
// Ch09.ProtectedMcpServer is the same endpoint behind OAuth.
// ============================================================================

using Ch09.McpHttpServer;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCatalogMcpOverHttp();   // AddMcpServer().WithHttpTransport(...).WithTools<...>()

var app = builder.Build();

// Routes the endpoint; without this line nothing is listening.
app.MapCatalogMcp();

app.Run("http://localhost:5000");
