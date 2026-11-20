using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;

namespace Ch09.ProtectedMcpServer;

/// <summary>
/// Who issues tokens and which resource they must be minted for.
/// </summary>
/// <param name="Resource">The MCP endpoint URL. Tokens must carry it as their audience.</param>
/// <param name="Issuer">The authorization server (its "iss" value).</param>
/// <param name="SigningKey">
/// Null in production: the keys come from the issuer's OpenID metadata. The offline
/// tests pass a locally generated key so that no identity provider is involved.
/// </param>
public sealed record ProtectedMcpSettings(string Resource, string Issuer, SecurityKey? SigningKey = null);

/// <summary>
/// The MCP server as an OAuth resource server: a bearer token is required for the
/// endpoint, and each tool also requires its own scope.
/// </summary>
public static class ProtectedMcp
{
    public static IServiceCollection AddProtectedCatalogMcp(this IServiceCollection services, ProtectedMcpSettings settings)
    {
        services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                // A 401 is written by the MCP handler: WWW-Authenticate carries the
                // resource_metadata URL a client needs to find the authorization server.
                options.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;   // keep "scope", "sub" as issued
                if (settings.SigningKey is null) options.Authority = settings.Issuer;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = settings.Issuer,
                    ValidAudience = settings.Resource,   // a token for another server is refused
                    IssuerSigningKey = settings.SigningKey,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            })
            .AddMcp(options => options.ResourceMetadata = new ProtectedResourceMetadata
            {
                Resource = settings.Resource,
                AuthorizationServers = [settings.Issuer],
                ScopesSupported = [.. CatalogScopes.All],
            });

        services.AddAuthorization(options =>
        {
            foreach (string scope in CatalogScopes.All)
            {
                options.AddPolicy(scope, policy => policy
                    .RequireAuthenticatedUser()
                    .RequireAssertion(context => CatalogScopes.Has(context.User, scope)));
            }
        });

        services
            .AddMcpServer(options => options.ServerInfo = new() { Name = "ContosoProductServer", Version = "1.0.0" })
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .AddAuthorizationFilters()               // [Authorize] on tools: filters tools/list, guards tools/call
            .WithTools([typeof(ProtectedCatalogTools)]);   // explicit: never the unprotected class
        return services;
    }

    public static WebApplication MapProtectedCatalogMcp(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMcp("/mcp").RequireAuthorization();   // no valid token, no MCP at all
        return app;
    }
}
