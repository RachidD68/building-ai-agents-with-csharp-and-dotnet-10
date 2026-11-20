// ============================================================================
// Protected MCP server (OAuth resource server over streamable HTTP)
//
// The catalog tools of Ch09.McpHttpServer, with two separate checks:
//   1. the endpoint: no valid bearer token for THIS resource -> 401, and the
//      WWW-Authenticate header points at the protected resource metadata
//      (/.well-known/oauth-protected-resource/mcp);
//   2. each tool: ProductLookup needs catalog.read, InventoryCheck needs
//      inventory.read. A tool the token cannot use is hidden from tools/list
//      and refused on tools/call.
//
// This host trusts a real authorization server. Set ProtectedMcp:Issuer (and
// ProtectedMcp:Resource if the URL differs) in appsettings.json or user
// secrets. The offline proof, with locally signed tokens and no identity
// provider, is Ch09.McpAccessTests.
// ============================================================================

using Ch09.ProtectedMcpServer;

var builder = WebApplication.CreateBuilder(args);

string resource = builder.Configuration["ProtectedMcp:Resource"] ?? "http://localhost:5002/mcp";
string? issuer = builder.Configuration["ProtectedMcp:Issuer"];
if (string.IsNullOrWhiteSpace(issuer))
{
    Console.Error.WriteLine("ProtectedMcp:Issuer is not set. Point it at your authorization server");
    Console.Error.WriteLine("(the 'iss' of the tokens it issues), then run again.");
    Console.Error.WriteLine("Offline proof without an identity provider: dotnet run --project src/Ch09.MCP/Ch09.McpAccessTests -- --self-test");
    return 2;
}

builder.Services.AddProtectedCatalogMcp(new ProtectedMcpSettings(resource, issuer));

var app = builder.Build();
app.MapProtectedCatalogMcp();

app.Run(new Uri(resource).GetLeftPart(UriPartial.Authority));
return 0;
