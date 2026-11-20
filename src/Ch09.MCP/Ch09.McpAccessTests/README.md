# MCP Access Tests (offline)

```bash
dotnet run --project src/Ch09.MCP/Ch09.McpAccessTests -- --self-test
```

Starts real Kestrel servers on `127.0.0.1` (a free port) with the samples' own setup files (linked, see the `.csproj`) and calls them with the MCP C# client:

1. **One tool set, two transports**: the stdio wiring (in-memory pipes) and streamable HTTP list the same tools, and a recording handler shows what the client sends: `server/discover` first, the protocol version and client information in `params._meta` on every request, no `Mcp-Session-Id`.
2. **Unprotected HTTP** (`Ch09.McpHttpServer`): a caller with no token lists and calls every tool. These lines are labelled `DEFECT REPRODUCED`.
3. **Protected server** (`Ch09.ProtectedMcpServer`): public resource metadata; 401 with `resource_metadata` for no token, wrong audience, expired, foreign signing key and wrong issuer; scope per tool on discovery and invocation; `catalog.read` gets prices without stock levels; the raw HTTP shape of a refused call.
   **3b. Discovery does not authorize invocation**: one client discovers `InventoryCheck` with a full-scope token; the same client and tool are then refused after the token is downgraded to `catalog.read`, and with 401 once it is gone.
4. **Destination guard** (`Ch09.McpConsumer/McpDestinationGuard.cs`): a token only for an allow-listed endpoint whose metadata names that endpoint. A client without the guard hands its token to a decoy server, which replays it against the real one (`DEFECT REPRODUCED`); through `CreateCheckedTransportOptionsAsync`, the consumer's own path, the decoy sees only the public metadata request and no token.

Tokens are HMAC-signed with a key generated for each run (`LocalIssuer.cs`); the issuer name uses the reserved `.invalid` domain. No identity provider, no network beyond loopback, no model. Tokens are never printed.

In Linux, run it in `mcr.microsoft.com/dotnet/aspnet:10.0-noble` (it needs the ASP.NET Core runtime).
