using Ch09.McpServer;
using ModelContextProtocol.AspNetCore;

namespace Ch09.McpHttpServer;

/// <summary>
/// The catalog tools over streamable HTTP, with no authentication: the
/// baseline that Ch09.McpAccessTests attacks and Ch09.ProtectedMcpServer fixes.
/// </summary>
public static class CatalogMcpEndpoint
{
    public static IServiceCollection AddCatalogMcpOverHttp(this IServiceCollection services)
    {
        services
            .AddMcpServer(options => options.ServerInfo = new() { Name = "ContosoProductServer", Version = "1.0.0" })
            // Since the 2026-07-28 protocol revision there is no Mcp-Session-Id on
            // the wire and the SDK defaults to stateless; set it so the intent shows.
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools([typeof(ProductCatalogTools)]);   // the class the stdio server serves
        return services;
    }

    // Pass the pattern: the no-argument MapMcp() serves at the site root ("/").
    public static IEndpointConventionBuilder MapCatalogMcp(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapMcp("/mcp");
}
