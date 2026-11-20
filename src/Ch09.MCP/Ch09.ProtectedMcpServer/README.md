# Protected MCP Server (OAuth resource server)

The catalog tools of `Ch09.McpHttpServer` behind two separate checks:

| Check | Where | Failure |
|-------|-------|---------|
| A bearer token issued by the configured issuer, for **this** resource (audience = the `/mcp` URL), not expired | `MapMcp("/mcp").RequireAuthorization()` + JWT bearer | HTTP 401, `WWW-Authenticate: Bearer resource_metadata="<base>/.well-known/oauth-protected-resource/mcp"` |
| The tool's own scope: `ProductLookup` needs `catalog.read`, `InventoryCheck` needs `inventory.read` | `[Authorize(Policy = ...)]` on each tool + `AddAuthorizationFilters()` | hidden from `tools/list`; `tools/call` gets a JSON-RPC error (HTTP 200) "Access forbidden" |
| What a scope may see: `catalog.read` is a price grant | `ProductLookup` returns a projection (`PriceInfo`) without the stock level | stock only through `InventoryCheck` (`inventory.read`) |

The protected resource metadata (`/.well-known/oauth-protected-resource/mcp`, no token needed) names the resource, the authorization server and the supported scopes (`AddMcp(o => o.ResourceMetadata = ...)`).

Files: `ProtectedMcp.cs` (the whole setup), `ProtectedCatalogTools.cs` (the tools with their scopes, over the catalog of the linked `ProductCatalogTools.cs`; `InventoryCheck` delegates, `ProductLookup` leaves the stock level out), `CatalogScopes.cs` (scope names and the `scope`/`scp` claim reader).

## Running

This host trusts a real authorization server. Set `ProtectedMcp:Issuer` (the `iss` of its tokens) and, if needed, `ProtectedMcp:Resource` in appsettings.json or user secrets; tokens must be issued with the resource URL as audience. Without an issuer the program prints what to set and exits with code 2. It has not been run against a real identity provider in this repository; the hosting-and-identity chapter covers one.

The offline proof, with locally signed tokens and no identity provider:

```bash
dotnet run --project src/Ch09.MCP/Ch09.McpAccessTests -- --self-test
```
